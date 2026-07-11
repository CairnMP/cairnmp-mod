// Polyfills des attributs de nullabilité requis par le générateur de source
// System.Text.Json sur net6.0. Nécessaire car les références Il2Cpp masquent
// les types runtime standard : le compilateur ne trouve plus le constructeur
// de NullableAttribute lors de la génération des contextes JSON.

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
