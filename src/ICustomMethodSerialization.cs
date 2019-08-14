namespace Microsoft.FrozenObjects
{
    using System;
    using System.Reflection;

    public interface ICustomMethodSerialization
    {
        Type TypeOfMethodObject { get; }

        MethodInfo GetMethodInfo(object o);
    }
}