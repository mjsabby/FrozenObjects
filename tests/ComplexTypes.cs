using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Collections.Extensions;

namespace Microsoft.FrozenObjects.UnitTests
{
    public enum LongEnum : long
    {
        Min = long.MinValue,
        Max = long.MaxValue
    }

    public enum IntEnum : int
    {
        Min = int.MinValue,
        Max = int.MaxValue
    }

    public enum UIntEnum : uint
    {
        Min = uint.MinValue,
        Max = uint.MaxValue
    }

    public class OuterStruct
    {
        public static void DoSomething()
        {
            Console.WriteLine("Called from a deserializer");
        }

        public struct GenericValueTypeWithReferences<T>
        {
            public string A;
            public byte B;
            public T C;
            public T[] D;
            public string E;
        }
    }

    public class GenericBaseClassForThings<T>
    {
        public List<T> BaseA;
        public int BaseB;
        public LongEnum BaseC;
        public string BaseD;
    }

    public class OuterClass
    {
        public struct FooStruct
        {
            public class GenericReferenceTypeWithInheritance<X, T, K, V> : GenericBaseClassForThings<DictionarySlim>
            {
                public T A;
                public K[] B;
                public V[] C;
                public V D;
                public X Y;
                public Recursive R;
                public Circular S;
            }
        }
    }

    public class Recursive
    {
        public Recursive Field;

        public string Data;
    }

    public class Bar
    {
        public Circular Foo;
    }

    public class Circular
    {
        public Bar Foo;
    }

    public class MyClassHasATypeField
    {
        public Type Field;
    }

    public class ClassHoldingAMethodPointer
    {
        public SlimMethodHandle MethodHandle;
    }

    public class SlimMethodHandle
    {
        public IntPtr FnPointer;
    }

    public class CustomMethodSerializer : ICustomMethodSerialization
    {
        private readonly object o;

        private readonly MethodInfo m;

        public CustomMethodSerializer(object o, MethodInfo m)
        {
            this.o = o;
            this.m = m;
        }

        public Type TypeOfMethodObject => typeof(SlimMethodHandle);

        public MethodInfo GetMethodInfo(object o)
        {
            if (ReferenceEquals(o, o))
            {
                return this.m;
            }

            throw new Exception("Not found");
        }
    }
}
