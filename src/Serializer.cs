﻿namespace Microsoft.FrozenObjects
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Reflection;
    using System.Reflection.Metadata;
    using System.Reflection.Metadata.Ecma335;
    using System.Reflection.PortableExecutable;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    public static class Serializer
    {
        public static void SerializeObject(object o, string outputDataPath, string outputAssemblyFilePath, string outputNamespace, string typeName, string methodName, Version version, Type methodType = null, byte[] privateKeyOpt = null)
        {
            Dictionary<Type, int> typeTokenMap = SerializeDataAndBuildTypeTokenMap(o, outputDataPath);

            (HashSet<Assembly> allAssemblies, HashSet<Type> allTypes) = ComputeAssemblyAndTypeClosure(typeTokenMap);

            MetadataBuilder metadataBuilder = CreateMetadataBuilder(Path.GetFileName(outputAssemblyFilePath), Path.GetFileNameWithoutExtension(outputAssemblyFilePath), version);
            
            Dictionary<Assembly, AssemblyReferenceHandle> assemblyReferenceHandleMap = AccumulateAssemblyReferenceHandles(metadataBuilder, allAssemblies);
            Dictionary<Type, TypeReferenceHandle> uniqueTypeRefMap = AccumulateTypeReferenceHandles(metadataBuilder, assemblyReferenceHandleMap, allTypes);
            Dictionary<Type, TypeSpecificationHandle> typeToTypeSpecMap = AccumulateTypeSpecificationHandles(metadataBuilder, uniqueTypeRefMap, typeTokenMap);

            SerializeCompanionAssembly(metadataBuilder, outputAssemblyFilePath, outputNamespace, typeName, methodName, typeTokenMap, typeToTypeSpecMap, privateKeyOpt);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void WriteObjectGraphToDisk(byte* stackAllocatedData, Stream stream, Dictionary<object, long> serializedObjectMap, Queue<object> objectQueue, RuntimeTypeHandleDictionary mtTokenMap, ref long lastReservedObjectEnd)
        {
            long nextObjectHeaderFilePointer = 0;

            while (objectQueue.Count != 0)
            {
                object o = objectQueue.Dequeue();
                ref TypeInfo typeInfo = ref mtTokenMap.GetOrAddValueRef(Type.GetTypeHandle(o));

                long mtToken;

                if (typeInfo != null)
                {
                    mtToken = typeInfo.MTToken;
                }
                else
                {
                    mtToken = mtTokenMap.Count - 1;
                    typeInfo = GetObjectInfo(o, mtToken);
                }

                long hashCode = (RuntimeHelpers.GetHashCode(o) & 0x0fffffff) | (1 << 27) | (1 << 26);

                fixed (byte* data = &GetRawData(o))
                {
                    MethodTable* mt;
                    if (IntPtr.Size == 8)
                    {
                        *(long*)stackAllocatedData = hashCode;
                        *(long*)(stackAllocatedData + 8) = mtToken;
                        stream.Write(new ReadOnlySpan<byte>(stackAllocatedData, 16));
                        mt = (MethodTable*)*(long*)(data - IntPtr.Size);
                    }
                    else
                    {
                        *(int*)stackAllocatedData = (int)hashCode;
                        *(int*)(stackAllocatedData + 4) = (int)mtToken;
                        stream.Write(new ReadOnlySpan<byte>(stackAllocatedData, 8));
                        mt = (MethodTable*)*(int*)(data - IntPtr.Size);
                    }
                    
                    uint flags = mt->Flags;
                    bool hasComponentSize = (flags & 0x80000000) == 0x80000000;

                    long objectSize = mt->BaseSize;

                    if (hasComponentSize)
                    {
                        var numComponents = (long)*(int*)data;
                        objectSize += numComponents * mt->ComponentSize;
                    }

                    var remainingSize = objectSize - IntPtr.Size - IntPtr.Size; // because we have already written the object header and MT Token

                    if (remainingSize <= int.MaxValue)
                    {
                        stream.Write(new ReadOnlySpan<byte>(data, (int)remainingSize));
                    }
                    else
                    {
                        WriteLargeObject(stream, data, remainingSize);
                    }

                    bool containsPointersOrCollectible = (flags & 0x10000000) == 0x10000000 || (flags & 0x1000000) == 0x1000000;
                    if (containsPointersOrCollectible)
                    {
                        var gcdesc = new GCDesc(typeInfo.GCDescData, typeInfo.GCDescSize);
                        gcdesc.EnumerateObject(data, objectSize, serializedObjectMap, objectQueue, stream, nextObjectHeaderFilePointer, ref lastReservedObjectEnd);
                    }

                    nextObjectHeaderFilePointer += objectSize + Padding(objectSize, IntPtr.Size);
                    stream.Seek(nextObjectHeaderFilePointer, SeekOrigin.Begin);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void WriteLargeObject(Stream stream, byte* data, long remainingSize)
        {
            var buffer = data + remainingSize;

            while (remainingSize != 0)
            {
                var chunkSize = Math.Min(64 * 1024, (int)remainingSize); // 64KB is a reasonable size
                stream.Write(new ReadOnlySpan<byte>(buffer - remainingSize, chunkSize));
                remainingSize -= chunkSize;
            }
        }

        /// <summary>
        /// Responsible for serializing the object graph (<param name="o"></param> to <param name="outputDataPath"></param>
        /// </summary>
        /// <remarks>
        /// Loads the native shared library that implements walking the object graph using GCDesc. The reason the functionality
        /// is in a native library is because we do a managed calli into this library as a mechanism to supress GC while we serialize
        /// the object graph to disk.
        ///
        /// Each serialized object has a placeholder token written in place of the MethodTable*.
        ///
        /// A tuple of each unique type we encounter and its placeholder token are returned from the native library. We in turn convert
        /// this data into a Dictionary`[System.Type, System.Int32], so that the remainder of the work can be done using System.Reflection
        ///
        /// So to summarize, we write the object graph to disk and get all unique types in the form of a Dictionary`[System.Type, System.Int32]
        ///
        /// Next, please read <seealso cref="ComputeAssemblyAndTypeClosure"/>.
        /// </remarks>
        /// <param name="root">object to serialize</param>
        /// <param name="outputDataPath">file path for the serialized object graph</param>
        private static unsafe Dictionary<Type, int> SerializeDataAndBuildTypeTokenMap(object root, string outputDataPath)
        {
            var serializedObjectMap = new Dictionary<object, long>();
            var mtTokenMap = new RuntimeTypeHandleDictionary(100);
            var objectQueue = new Queue<object>();

            objectQueue.Enqueue(root);

            long lastReservedObjectEnd = GetObjectSize(root);
            lastReservedObjectEnd += Padding(lastReservedObjectEnd, IntPtr.Size);

            byte* stackAllocatedData = stackalloc byte[16];

            using (var stream = new FileStream(outputDataPath, FileMode.Create, FileAccess.Write))
            {
                WriteObjectGraphToDisk(stackAllocatedData, stream, serializedObjectMap, objectQueue, mtTokenMap, ref lastReservedObjectEnd);
            }

            var typeTokenMap = new Dictionary<Type, int>(mtTokenMap.Count);

            foreach (var entry in mtTokenMap)
            {
                typeTokenMap.Add(Type.GetTypeFromHandle(entry.Key), (int)entry.Value.MTToken);
            }

            return typeTokenMap;
        }

        /// <summary>
        /// Responsible for finding the closure of all Assembly and Types reachable from the original
        /// set of Types that were part of the input object graph.
        /// </summary>
        /// <remarks>
        ///
        /// We first put all the types we saw in the object walk into a queue, then find all the base types, and
        /// if the types were nested, the heirarchy of those types. If the type was generic then their arguments, which
        /// in turn go find all theirs, so on and so forth.
        ///
        /// Ultimately the goal of this function is to get all the types and assemblies that could be needed when encoding the signatures.
        ///
        /// It is quite possible that some of these types may not require to be encoded, and that's ok.
        ///
        /// Next, please read <seealso cref="AccumulateAssemblyReferenceHandles"/>.
        /// </remarks>
        /// <param name="typeTokenMap">The unique set of types that were in the input object graph</param>
        private static Tuple<HashSet<Assembly>, HashSet<Type>> ComputeAssemblyAndTypeClosure(Dictionary<Type, int> typeTokenMap)
        {
            var typeQueue = new Queue<Type>();

            foreach (var entry in typeTokenMap)
            {
                typeQueue.Enqueue(entry.Key);
            }

            var allTypes = new HashSet<Type>();
            var allAssemblies = new HashSet<Assembly>();

            while (typeQueue.Count != 0)
            {
                var type = typeQueue.Peek();
                if (!allTypes.Contains(type))
                {
                    if (type.IsPointer || type.IsByRef || type.IsByRefLike || type.IsCOMObject) // || type.IsCollectible ??
                    {
                        throw new NotSupportedException();
                    }

                    if (type.IsArray)
                    {
                        typeQueue.Enqueue(type.GetElementType());
                    }
                    else
                    {
                        var declaringType = type.DeclaringType;
                        if (declaringType != null)
                        {
                            typeQueue.Enqueue(declaringType);
                        }

                        var baseType = type.BaseType;
                        if (baseType != null)
                        {
                            typeQueue.Enqueue(baseType);
                        }

                        if (type.IsGenericType)
                        {
                            var typeArgs = type.GenericTypeArguments;
                            for (int i = 0; i < typeArgs.Length; ++i)
                            {
                                typeQueue.Enqueue(typeArgs[i]);
                            }
                        }
                    }

                    var typeAssembly = type.Assembly;
                    if (!allAssemblies.Contains(typeAssembly))
                    {
                        allAssemblies.Add(typeAssembly);
                    }

                    allTypes.Add(type);
                }

                typeQueue.Dequeue();
            }

            return new Tuple<HashSet<Assembly>, HashSet<Type>>(allAssemblies, allTypes);
        }

        /// <summary>
        /// Responsible for taking all the unique Assembly objects and just blindly writing an AssemblyRef into the metadata
        /// </summary>
        /// <remarks>
        ///
        /// Nothing particularly interesting here, please read <seealso cref="AccumulateTypeReferenceHandles"/>
        ///
        /// </remarks>
        /// <param name="metadataBuilder">the metadata builder</param>
        /// <param name="allAssemblies">unique set of assemblies encountered in our closure walk</param>
        private static Dictionary<Assembly, AssemblyReferenceHandle> AccumulateAssemblyReferenceHandles(MetadataBuilder metadataBuilder, HashSet<Assembly> allAssemblies)
        {
            var assemblyReferenceHandleMap = new Dictionary<Assembly, AssemblyReferenceHandle>();

            foreach (var assembly in allAssemblies)
            {
                var assemblyName = assembly.GetName();
                var assemblyNameStringHandle = metadataBuilder.GetOrAddString(assemblyName.Name);

                var publicKeyTokenBlobHandle = default(BlobHandle);
                var publicKeyToken = assemblyName.GetPublicKeyToken();
                if (publicKeyToken != null)
                {
                    publicKeyTokenBlobHandle = metadataBuilder.GetOrAddBlob(publicKeyToken);
                }

                StringHandle cultureStringHandle = default;
                if (assemblyName.CultureName != null)
                {
                    cultureStringHandle = metadataBuilder.GetOrAddString(assemblyName.CultureName);
                }

                assemblyReferenceHandleMap.Add(assembly, metadataBuilder.AddAssemblyReference(assemblyNameStringHandle, assemblyName.Version, cultureStringHandle, publicKeyTokenBlobHandle, default, default));
            }

            return assemblyReferenceHandleMap;
        }

        /// <summary>
        /// Responsible for taking all the unique Type objects and putting the TypeRefs into the metadata
        /// </summary>
        /// <remarks>
        ///
        /// A couple of interesting things to point out:
        ///
        /// (1) Notice that nested types are handled by putting them into this <code>typeList</code>, and then the list in walked in reverse order
        ///     so that all the TypeRefs can be found by subsequent dictionary lookups.
        ///
        /// (2) The resolutionScope is taken care of by checking if there is a declaring type or not, and by (1) we'll ensure that we always do it
        ///     an order that those dictionary lookups can be satisfied. And if you're a nested type your namespace must be empty. System.Reflection
        ///     doesn't return an empty namespace, so we had to do some work there.
        ///
        /// (3) Note that we need to get ensure we're working with the generic type definition, otherwise we'll serialize the same typerefs multiple times
        ///     and fail PE Verification.
        ///
        /// After this function completes we can be certain that all typerefs & assemblyrefs needed for the next phase, encoding of the typespec signature
        /// are a dictionary lookup away.
        ///
        /// Please read <seealso cref="AccumulateTypeSpecificationHandles"/>
        ///
        /// </remarks>
        /// <param name="metadataBuilder"></param>
        /// <param name="assemblyReferenceHandleMap"></param>
        /// <param name="allTypes"></param>
        private static Dictionary<Type, TypeReferenceHandle> AccumulateTypeReferenceHandles(MetadataBuilder metadataBuilder, Dictionary<Assembly, AssemblyReferenceHandle> assemblyReferenceHandleMap, HashSet<Type> allTypes)
        {
            var uniqueTypeRefMap = new Dictionary<Type, TypeReferenceHandle>();

            foreach (var type in allTypes)
            {
                // If we don't skip arrays here we end up adding bogus TypeRefs that don't actually exist.
                // e.g. System.Int32[] which is just a name of a plain type, and of course doesn't exist.
                if (type.IsArray)
                {
                    continue;
                }

                var typeList = new List<Type> { type };
                {
                    var tmp = type;
                    while (tmp.DeclaringType != null)
                    {
                        typeList.Add(tmp.DeclaringType);
                        tmp = tmp.DeclaringType;
                    }
                }

                for (int i = typeList.Count - 1; i > -1; --i)
                {
                    var t = typeList[i];
                    if (t.IsGenericType)
                    {
                        t = t.GetGenericTypeDefinition();
                    }

                    if (!uniqueTypeRefMap.ContainsKey(t))
                    {
                        var declaringType = t.DeclaringType;
                        var resolutionScope = declaringType == null ? (EntityHandle)assemblyReferenceHandleMap[t.Assembly] : (EntityHandle)uniqueTypeRefMap[declaringType];

                        var @namespace = default(StringHandle);
                        if (declaringType == null && !string.IsNullOrEmpty(t.Namespace))
                        {
                            @namespace = metadataBuilder.GetOrAddString(t.Namespace);
                        }

                        uniqueTypeRefMap.Add(t, metadataBuilder.AddTypeReference(resolutionScope, @namespace, metadataBuilder.GetOrAddString(t.Name)));
                    }
                }
            }

            return uniqueTypeRefMap;
        }

        /// <summary>
        /// Responsible for encoding the typespec signatures for each of the unique types we encountered in the input object graph.
        /// </summary>
        /// <remarks>
        ///
        /// At this point we have all the pieces needed to encoder a TypeSpec signature for each unique type we saw in the object graph.
        ///
        /// We have to use the short-form signatures for some of those primitive type codes, but besides that we just handle the ECMA-335 II.23.2.12 Type
        /// production rules except for the stuff we know we don't want or can't encounter on a heap.
        ///
        /// This includes:
        ///
        /// FNPTR MethodDefOrRefSig
        /// MVAR number
        /// VAR number
        ///
        /// In addition to that I don't think we want these:
        ///
        /// PTR Type
        /// PTR VOID
        ///
        /// You can see all the stuff we handle here, <seealso cref="HandleType"/>
        /// and next on the reading list is <seealso cref="SerializeCompanionAssembly"/>
        /// 
        /// </remarks>
        /// 
        /// <param name="metadataBuilder">the metadata builder</param>
        /// <param name="uniqueTypeRefMap">dictionary of all possible types</param>
        /// <param name="typeTokenMap">the actual unique set of types we encountered</param>
        /// <returns></returns>
        private static Dictionary<Type, TypeSpecificationHandle> AccumulateTypeSpecificationHandles(MetadataBuilder metadataBuilder, Dictionary<Type, TypeReferenceHandle> uniqueTypeRefMap, Dictionary<Type, int> typeTokenMap)
        {
            var primitiveTypeCodeMap = new Dictionary<Type, PrimitiveTypeCode>
            {
                { typeof(bool), PrimitiveTypeCode.Boolean },
                { typeof(char), PrimitiveTypeCode.Char },
                { typeof(sbyte), PrimitiveTypeCode.SByte },
                { typeof(byte), PrimitiveTypeCode.Byte },
                { typeof(short), PrimitiveTypeCode.Int16 },
                { typeof(ushort), PrimitiveTypeCode.UInt16 },
                { typeof(int), PrimitiveTypeCode.Int32 },
                { typeof(uint), PrimitiveTypeCode.UInt32 },
                { typeof(long), PrimitiveTypeCode.Int64 },
                { typeof(ulong), PrimitiveTypeCode.UInt64 },
                { typeof(float), PrimitiveTypeCode.Single },
                { typeof(double), PrimitiveTypeCode.Double },
                { typeof(string), PrimitiveTypeCode.String },
                { typeof(IntPtr), PrimitiveTypeCode.IntPtr }, // We don't really want these, except for that custom MethodInfo support
                { typeof(UIntPtr), PrimitiveTypeCode.UIntPtr }, // We don't really want these, except for that custom MethodInfo support
                { typeof(object), PrimitiveTypeCode.Object }
            };

            var typeToTypeSpecMap = new Dictionary<Type, TypeSpecificationHandle>();
            foreach (var type in typeTokenMap.Keys)
            {
                var blobBuilder = new BlobBuilder();
                var encoder = new BlobEncoder(blobBuilder).TypeSpecificationSignature();

                HandleType(type, ref encoder, primitiveTypeCodeMap, uniqueTypeRefMap);

                typeToTypeSpecMap.Add(type, metadataBuilder.AddTypeSpecification(metadataBuilder.GetOrAddBlob(blobBuilder)));
            }

            return typeToTypeSpecMap;
        }

        /**
         * Type ::=
         *   BOOLEAN | CHAR | I1 | U1 | I2 | U2 | I4 | U4 | I8 | U8 | R4 | R8 | I | U | OBJECT | STRING
         * | ARRAY Type ArrayShape
         * | SZARRAY Type
         * | GENERICINST (CLASS | VALUETYPE) TypeRefOrSpecEncoded GenArgCount Type*
         * | (CLASS | VALUETYPE) TypeRefOrSpecEncoded
         */
        private static void HandleType(Type type, ref SignatureTypeEncoder encoder, Dictionary<Type, PrimitiveTypeCode> primitiveTypeCodeMap, Dictionary<Type, TypeReferenceHandle> uniqueTypeRefMap)
        {
            if (primitiveTypeCodeMap.TryGetValue(type, out var primitiveTypeCode))
            {
                // BOOLEAN | CHAR | I1 | U1 | I2 | U2 | I4 | U4 | I8 | U8 | R4 | R8 | I | U | OBJECT | STRING
                encoder.PrimitiveType(primitiveTypeCode);
            }
            else if (type.IsVariableBoundArray)
            {
                // ARRAY Type ArrayShape
                HandleArray(ref encoder);
            }
            else if (type.IsSZArray)
            {
                // SZARRAY Type
                HandleSZArray(ref encoder);
            }
            else if (type.IsConstructedGenericType)
            {
                // GENERICINST (CLASS | VALUETYPE) TypeRefOrSpecEncoded GenArgCount Type*
                HandleGenericInst(ref encoder);
            }
            else if (type.IsPointer || type.IsCollectible || type.IsCOMObject)
            {
                // what other things can be on the heap but are not caught by this check?
                throw new NotSupportedException();
            }
            else
            {
                // CLASS TypeRefOrSpecEncoded | VALUETYPE TypeRefOrSpecEncoded
                encoder.Type(uniqueTypeRefMap[type], type.IsValueType);
            }

            void HandleSZArray(ref SignatureTypeEncoder e)
            {
                e.SZArray();
                HandleType(type.GetElementType(), ref e, primitiveTypeCodeMap, uniqueTypeRefMap);
            }

            void HandleArray(ref SignatureTypeEncoder e)
            {
                e.Array(out var elementTypeEncoder, out var arrayShapeEncoder);
                HandleType(type.GetElementType(), ref elementTypeEncoder, primitiveTypeCodeMap, uniqueTypeRefMap);

                var rank = type.GetArrayRank();
                var arr = new int[rank];
                var imm = ImmutableArray.Create(arr);

                arrayShapeEncoder.Shape(rank, ImmutableArray<int>.Empty, imm); // just so we match what the C# compiler generates for mdarrays
            }

            void HandleGenericInst(ref SignatureTypeEncoder e)
            {
                var genericTypeArguments = type.GenericTypeArguments;
                var genericTypeArgumentsEncoder = e.GenericInstantiation(uniqueTypeRefMap[type.GetGenericTypeDefinition()], genericTypeArguments.Length, type.IsValueType);

                for (int i = 0; i < genericTypeArguments.Length; ++i)
                {
                    var genericTypeArgumentEncoder = genericTypeArgumentsEncoder.AddArgument();
                    HandleType(genericTypeArguments[i], ref genericTypeArgumentEncoder, primitiveTypeCodeMap, uniqueTypeRefMap);
                }
            }
        }

        /// <summary>
        /// Responsible for serializing the MethodDefinition that allows the consumer to load the associated blob.
        /// </summary>
        /// <remarks>
        ///
        /// Generates a method whose signature is `object MethodName(string filePath);`
        ///
        /// The file path is the path of the blob associated with this assembly.
        ///
        /// The method that this piece of code generates has the glue required for the deserialization to work. Essentially it allocates
        /// an array of RuntimeTypeHandles, and the index of the array is the the placeholder token we stuffed into the serialized object we wrote to disk.
        ///
        /// At deserialization time, the user calls this method which does an `ldtoken TypeSpec` for each of the unique types we saw at serialization time.
        /// And then the deserializer uses this information to do the fixups as it deserializes using GCDesc.
        ///
        /// </remarks>
        /// <param name="metadataBuilder">the metadata builder</param>
        /// <param name="outputAssemblyFilePath">the file path of the assembly</param>
        /// <param name="outputNamespace">the namespace of the assembly</param>
        /// <param name="typeName">the type of the method</param>
        /// <param name="methodName">the name of the method</param>
        /// <param name="typeTokenMap">the type to token place holder map</param>
        /// <param name="typeToTypeSpecMap">the type to typespec map</param>
        /// <param name="privateKeyOpt">optional key</param>
        private static void SerializeCompanionAssembly(MetadataBuilder metadataBuilder, string outputAssemblyFilePath, string outputNamespace, string typeName, string methodName, Dictionary<Type, int> typeTokenMap, Dictionary<Type, TypeSpecificationHandle> typeToTypeSpecMap, byte[] privateKeyOpt)
        {
            var netstandardAssemblyRef = metadataBuilder.AddAssemblyReference(metadataBuilder.GetOrAddString("netstandard"), new Version(2, 0, 0, 0), default, metadataBuilder.GetOrAddBlob(new byte[] { 0xCC, 0x7B, 0x13, 0xFF, 0xCD, 0x2D, 0xDD, 0x51 }), default, default);
            var systemObjectTypeRef = metadataBuilder.AddTypeReference(netstandardAssemblyRef, metadataBuilder.GetOrAddString("System"), metadataBuilder.GetOrAddString("Object"));

            var frozenObjectSerializerAssemblyRef = metadataBuilder.AddAssemblyReference(
                name: metadataBuilder.GetOrAddString("Microsoft.FrozenObjects"),
                version: new Version(1, 0, 0, 0),
                culture: default,
                publicKeyOrToken: default,
                flags: default,
                hashValue: default);

            var runtimeTypeHandleObjectRef = metadataBuilder.AddTypeReference(
                netstandardAssemblyRef,
                metadataBuilder.GetOrAddString("System"),
                metadataBuilder.GetOrAddString("RuntimeTypeHandle"));

            var deserializerRef = metadataBuilder.AddTypeReference(
                frozenObjectSerializerAssemblyRef,
                metadataBuilder.GetOrAddString("Microsoft.FrozenObjects"),
                metadataBuilder.GetOrAddString("Deserializer"));

            var ilBuilder = new BlobBuilder();

            var frozenObjectDeserializerSignature = new BlobBuilder();

            new BlobEncoder(frozenObjectDeserializerSignature).
                MethodSignature().
                Parameters(2,
                    returnType => returnType.Type().Object(),
                    parameters =>
                    {
                        parameters.AddParameter().Type().SZArray().Type(runtimeTypeHandleObjectRef, true);
                        parameters.AddParameter().Type().String();
                    });

            var deserializeMemberRef = metadataBuilder.AddMemberReference(
                deserializerRef,
                metadataBuilder.GetOrAddString("Deserialize"),
                metadataBuilder.GetOrAddBlob(frozenObjectDeserializerSignature));

            var mainSignature = new BlobBuilder();

            new BlobEncoder(mainSignature).
                MethodSignature().
                Parameters(1, returnType => returnType.Type().Object(), parameters =>
                {
                    parameters.AddParameter().Type().String();
                });

            var codeBuilder = new BlobBuilder();

            var il = new InstructionEncoder(codeBuilder);
            il.LoadConstantI4(typeTokenMap.Count);
            il.OpCode(ILOpCode.Newarr);
            il.Token(runtimeTypeHandleObjectRef);

            foreach (var entry in typeTokenMap)
            {
                il.OpCode(ILOpCode.Dup);
                il.LoadConstantI4(entry.Value);
                il.OpCode(ILOpCode.Ldtoken);
                il.Token(typeToTypeSpecMap[entry.Key]);
                il.OpCode(ILOpCode.Stelem);
                il.Token(runtimeTypeHandleObjectRef);
            }

            il.LoadArgument(0);
            il.OpCode(ILOpCode.Call);
            il.Token(deserializeMemberRef);
            il.OpCode(ILOpCode.Ret);

            var methodBodyStream = new MethodBodyStreamEncoder(ilBuilder);

            int mainBodyOffset = methodBodyStream.AddMethodBody(il);
            codeBuilder.Clear();

            var mainMethodDef = metadataBuilder.AddMethodDefinition(
                            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                            MethodImplAttributes.IL | MethodImplAttributes.Managed,
                            metadataBuilder.GetOrAddString(methodName),
                            metadataBuilder.GetOrAddBlob(mainSignature),
                            mainBodyOffset,
                            parameterList: default);

            metadataBuilder.AddTypeDefinition(
                default,
                default,
                metadataBuilder.GetOrAddString("<Module>"),
                baseType: default,
                fieldList: MetadataTokens.FieldDefinitionHandle(1),
                methodList: mainMethodDef);

            metadataBuilder.AddTypeDefinition(
                TypeAttributes.Class | TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.AutoLayout | TypeAttributes.AnsiClass | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
                metadataBuilder.GetOrAddString(outputNamespace),
                metadataBuilder.GetOrAddString(typeName),
                systemObjectTypeRef,
                fieldList: MetadataTokens.FieldDefinitionHandle(1),
                methodList: mainMethodDef);

            using (var fs = new FileStream(outputAssemblyFilePath, FileMode.Create, FileAccess.Write))
            {
                WritePEImage(fs, metadataBuilder, ilBuilder, privateKeyOpt);
            }
        }

        private static MetadataBuilder CreateMetadataBuilder(string outputModuleName, string outputAssemblyName, Version version)
        {
            var metadataBuilder = new MetadataBuilder();
            metadataBuilder.AddModule(0, metadataBuilder.GetOrAddString(outputModuleName), metadataBuilder.GetOrAddGuid(Guid.NewGuid()), default, default);
            metadataBuilder.AddAssembly(metadataBuilder.GetOrAddString(outputAssemblyName), version, default, default, default, AssemblyHashAlgorithm.Sha1);
            return metadataBuilder;
        }

        private static void WritePEImage(Stream peStream, MetadataBuilder metadataBuilder, BlobBuilder ilBuilder, byte[] privateKeyOpt, Blob mvidFixup = default)
        {
            var peBuilder = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll), new MetadataRootBuilder(metadataBuilder), ilBuilder, entryPoint: default, flags: CorFlags.ILOnly | (privateKeyOpt != null ? CorFlags.StrongNameSigned : 0), deterministicIdProvider: null);

            var peBlob = new BlobBuilder();

            var contentId = peBuilder.Serialize(peBlob);

            if (!mvidFixup.IsDefault)
            {
                new BlobWriter(mvidFixup).WriteGuid(contentId.Guid);
            }

            peBlob.WriteContentTo(peStream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe TypeInfo GetObjectInfo(object obj, long mtToken)
        {
            var typeInfo = new TypeInfo { MTToken = mtToken };
            var mt = Unsafe.Add(ref Unsafe.As<byte, IntPtr>(ref GetRawData(obj)), -1);
            uint flags = ((MethodTable*)mt)->Flags;

            bool containsPointersOrCollectible = (flags & 0x10000000) == 0x10000000 || (flags & 0x1000000) == 0x1000000;

            if (containsPointersOrCollectible)
            {
                int entries = *(int*)(mt - IntPtr.Size);
                if (entries < 0)
                {
                    entries -= entries;
                }

                int slots = 1 + entries * 2;

                typeInfo.GCDescData = new IntPtr((byte*)mt - (slots * IntPtr.Size));
                typeInfo.GCDescSize = slots * IntPtr.Size;
            }

            return typeInfo;
        }

        private static unsafe long GetObjectSize(object obj)
        {
            var mt = (MethodTable*)Unsafe.Add(ref Unsafe.As<byte, IntPtr>(ref GetRawData(obj)), -1);
            uint flags = mt->Flags;
            bool hasComponentSize = (flags & 0x80000000) == 0x80000000;

            long objectSize = mt->BaseSize;

            if (hasComponentSize)
            {
                var numComponents = (long)Unsafe.As<byte, int>(ref GetRawData(obj));
                objectSize += numComponents * mt->ComponentSize;
            }

            return objectSize;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte GetRawData(object o)
        {
            return ref Unsafe.As<RawData>(o).Data;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Padding(long num, int align)
        {
            return (int)(0 - num & (align - 1));
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct MethodTable
        {
            [FieldOffset(0)]
            public ushort ComponentSize;

            [FieldOffset(0)]
            public uint Flags;

            [FieldOffset(4)]
            public uint BaseSize;
        }

        private readonly unsafe ref struct GCDesc
        {
            private readonly IntPtr data;

            private readonly int size;

            public GCDesc(IntPtr data, int size)
            {
                this.data = data;
                this.size = size;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GetNumSeries()
            {
                return (int)Marshal.ReadIntPtr(this.data + this.size - IntPtr.Size);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GetHighestSeries()
            {
                return this.size - IntPtr.Size * 3;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GetLowestSeries()
            {
                return this.size - ComputeSize(this.GetNumSeries());
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GetSeriesSize(int curr)
            {
                return (int)Marshal.ReadIntPtr(this.data + curr);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ulong GetSeriesOffset(int curr)
            {
                return (ulong)Marshal.ReadIntPtr(this.data + curr + IntPtr.Size);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public uint GetPointers(int curr, int i)
            {
                int offset = i * IntPtr.Size;
                return IntPtr.Size == 8 ? (uint)Marshal.ReadInt32(this.data + curr + offset) : (uint)Marshal.ReadInt16(this.data + curr + offset);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public uint GetSkip(int curr, int i)
            {
                int offset = i * IntPtr.Size + IntPtr.Size / 2;
                return IntPtr.Size == 8 ? (uint)Marshal.ReadInt32(this.data + curr + offset) : (uint)Marshal.ReadInt16(this.data + curr + offset);
            }

            public void EnumerateObject(byte* o, long objectSize, Dictionary<object, long> serializedObjectMap, Queue<object> objectQueue, Stream stream, long objectHeaderStart, ref long lastReservedObjectEnd)
            {
                int series = this.GetNumSeries();
                int highest = this.GetHighestSeries();
                int curr = highest;

                if (series > 0)
                {
                    int lowest = this.GetLowestSeries();
                    do
                    {
                        ulong offset = this.GetSeriesOffset(curr);
                        ulong stop = offset + (ulong)(this.GetSeriesSize(curr) + objectSize);

                        while (offset < stop)
                        {
                            EachObjectReference(o, (int)offset, serializedObjectMap, objectQueue, stream, objectHeaderStart, ref lastReservedObjectEnd);
                            offset += (uint)IntPtr.Size;
                        }

                        curr -= IntPtr.Size * 2;
                    } while (curr >= lowest);
                }
                else
                {
                    ulong offset = this.GetSeriesOffset(curr);
                    while (offset < (ulong)(objectSize - IntPtr.Size))
                    {
                        for (int i = 0; i > series; i--)
                        {
                            uint nptrs = this.GetPointers(curr, i);
                            uint skip = this.GetSkip(curr, i);

                            ulong stop = offset + nptrs * (ulong)IntPtr.Size;
                            do
                            {
                                EachObjectReference(o, (int)offset, serializedObjectMap, objectQueue, stream, objectHeaderStart, ref lastReservedObjectEnd);
                                offset += (ulong)IntPtr.Size;
                            } while (offset < stop);

                            offset += skip;
                        }
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static int ComputeSize(int series)
            {
                return IntPtr.Size + series * IntPtr.Size * 2;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            private static void EachObjectReference(byte* o, int fieldOffset, Dictionary<object, long> serializedObjectMap, Queue<object> objectQueue, Stream stream, long objectHeaderStart, ref long lastReservedObjectEnd)
            {
                ref var objectReference = ref Unsafe.AsRef<object>(o + fieldOffset - IntPtr.Size);
                if (objectReference == null)
                {
                    return;
                }

                if (!serializedObjectMap.TryGetValue(objectReference, out var objectReferenceDiskOffset))
                {
                    objectReferenceDiskOffset = lastReservedObjectEnd + IntPtr.Size; // + IntPtr.Size because object references point to the MT*
                    var objectSize = GetObjectSize(objectReference);
                    lastReservedObjectEnd += objectSize + Padding(objectSize, IntPtr.Size);

                    serializedObjectMap.Add(objectReference, objectReferenceDiskOffset);
                    objectQueue.Enqueue(objectReference);
                }

                stream.Seek(objectHeaderStart + IntPtr.Size + fieldOffset, SeekOrigin.Begin);
                stream.Write(new ReadOnlySpan<byte>(&objectReferenceDiskOffset, IntPtr.Size));
            }
        }

        private class RawData
        {
            public byte Data;
        }

        private class TypeInfo
        {
            public long MTToken;

            public IntPtr GCDescData;

            public int GCDescSize;
        }

        private class RuntimeTypeHandleDictionary
        {
            private int count;  // 0-based index into this.entries of head of free chain: -1 means empty

            private int freeList = -1;  // 1-based index into this.entries; 0 means empty 

            private int[] buckets;

            private Entry[] entries;

            public Entry[] Entries
            {
                get { return this.entries; }
            }

            public int[] Buckets
            {
                get { return this.buckets; }
            }

            public struct Entry
            {
                public RuntimeTypeHandle key;

                public TypeInfo value;

                public int next;
            }

            public RuntimeTypeHandleDictionary(int capacity)
            {
                if (capacity < 2)
                {
                    capacity = 2;
                }

                int PowerOf2(int v)
                {
                    if ((v & (v - 1)) == 0)
                    {
                        return v;
                    }

                    int i = 2;

                    while (i < v)
                    {
                        i <<= 1;
                    }

                    return i;
                }

                capacity = PowerOf2(capacity);
                this.buckets = new int[capacity];
                this.entries = new Entry[capacity];
            }

            public int Count => this.count;

            public ref TypeInfo GetOrAddValueRef(RuntimeTypeHandle key)
            {
                Entry[] e = this.entries;

                int bucketIndex = key.GetHashCode() & (this.buckets.Length - 1);
                for (int i = this.buckets[bucketIndex] - 1; (uint)i < (uint)e.Length; i = e[i].next)
                {
                    if (key.Value == e[i].key.Value)
                    {
                        return ref e[i].value;
                    }
                }

                return ref AddKey(key, bucketIndex);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private ref TypeInfo AddKey(RuntimeTypeHandle key, int bucketIndex)
            {
                Entry[] e = this.entries;
                int entryIndex;
                if (this.freeList != -1)
                {
                    entryIndex = this.freeList;
                    this.freeList = -3 - e[this.freeList].next;
                }
                else
                {
                    if (this.count == e.Length || e.Length == 1)
                    {
                        e = Resize();
                        bucketIndex = key.GetHashCode() & (this.buckets.Length - 1);
                        // entry indexes were not changed by Resize
                    }
                    entryIndex = this.count;
                }

                e[entryIndex].key = key;
                e[entryIndex].next = this.buckets[bucketIndex] - 1;
                this.buckets[bucketIndex] = entryIndex + 1;
                this.count++;
                return ref e[entryIndex].value;
            }

            private Entry[] Resize()
            {
                int c = this.count;
                int newSize = this.entries.Length * 2;
                if ((uint)newSize > (uint)int.MaxValue) // uint cast handles overflow
                {
                    throw new InvalidOperationException("Overflow");
                }

                var e = new Entry[newSize];
                Array.Copy(this.entries, 0, e, 0, c);

                var newBuckets = new int[e.Length];
                while (c-- > 0)
                {
                    int bucketIndex = e[c].key.GetHashCode() & (newBuckets.Length - 1);
                    e[c].next = newBuckets[bucketIndex] - 1;
                    newBuckets[bucketIndex] = c + 1;
                }

                this.buckets = newBuckets;
                this.entries = e;

                return e;
            }

            public Enumerator GetEnumerator() => new Enumerator(this);

            public struct Enumerator : IEnumerator<KeyValuePair<RuntimeTypeHandle, TypeInfo>>
            {
                private readonly RuntimeTypeHandleDictionary dictionary;

                private int index;

                private int count;

                private KeyValuePair<RuntimeTypeHandle, TypeInfo> current;

                internal Enumerator(RuntimeTypeHandleDictionary dictionary)
                {
                    this.dictionary = dictionary;
                    this.index = 0;
                    this.count = this.dictionary.count;
                    this.current = default;
                }

                public bool MoveNext()
                {
                    if (this.count == 0)
                    {
                        this.current = default;
                        return false;
                    }

                    this.count--;

                    while (this.dictionary.entries[this.index].next < -1)
                    {
                        this.index++;
                    }

                    this.current = new KeyValuePair<RuntimeTypeHandle, TypeInfo>(this.dictionary.entries[this.index].key, this.dictionary.entries[this.index++].value);
                    return true;
                }

                public KeyValuePair<RuntimeTypeHandle, TypeInfo> Current => this.current;

                object IEnumerator.Current => this.current;

                void IEnumerator.Reset()
                {
                    this.index = 0;
                    this.count = this.dictionary.count;
                    this.current = default;
                }

                public void Dispose()
                {
                }
            }
        }
    }
}