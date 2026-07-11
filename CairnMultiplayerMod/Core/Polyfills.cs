// Polyfills for the nullability attributes required by the System.Text.Json
// source generator on net6.0. Needed because the Il2Cpp references shadow the
// standard runtime types: the compiler can no longer find the NullableAttribute
// constructor when generating the JSON contexts.

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Event | AttributeTargets.Field |
        AttributeTargets.GenericParameter | AttributeTargets.Parameter |
        AttributeTargets.Property | AttributeTargets.ReturnValue,
        AllowMultiple = false,
        Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;

        public NullableAttribute(byte value)
        {
            NullableFlags = new[] { value };
        }

        public NullableAttribute(byte[] value)
        {
            NullableFlags = value;
        }
    }

    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Delegate | AttributeTargets.Interface |
        AttributeTargets.Method | AttributeTargets.Struct,
        AllowMultiple = false,
        Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;

        public NullableContextAttribute(byte value)
        {
            Flag = value;
        }
    }
}
