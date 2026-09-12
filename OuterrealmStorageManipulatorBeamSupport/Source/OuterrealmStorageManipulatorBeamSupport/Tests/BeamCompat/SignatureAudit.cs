using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

internal static class SignatureAudit
{
    // 直接检查用户提供的 DLL 元数据，不加载游戏或执行第三方静态构造器。
    // 期望表只列出适配器实际绑定并依赖的边界；签名不符时适配器安装会整组回滚，
    // 因此这里的严格核对就是「适配是否仍然有效」的第一道证明。
    internal static void Run(string path)
    {
        var expected = new Dictionary<string, string>
        {
            ["BeamManipulatorUtility.TryFindStorageDestinationFor"] = "Boolean(IBeamOperator,Thing,HashSet<IntVec3>,Int32,IntVec3&):op,thing,excludedDestinations,ownerKey,destination",
            ["BeamManipulatorUtility.CanBeamTransferThing"] = "Boolean(IBeamOperator,Thing,Int32):op,thing,ownerKey",
            ["BeamManipulatorUtility.TryClaimAndEnqueue"] = "Boolean(BeamTransfer,List<BeamTransfer>,HashSet<Thing>,Int32):transfer,destinationQueue,excludedThings,ownerKey",
            ["BeamManipulatorUtility.FinishTransfer"] = "Boolean(IBeamOperator,Thing,BeamTransfer,IntVec3):op,carriedThing,transfer,fallbackCell",
            ["BeamManipulatorUtility.IsBeamStorageGroupAllowed"] = "Boolean(SlotGroup):group",
            ["Building_BeamManipulator.TryLiftForTransfer"] = "Boolean(IBeamOperator,BeamTransfer,Thing&):op,transfer,carriedThing",
            ["Building_BeamManipulator.ExtractThingForTransfer"] = "Thing(BeamTransfer):transfer",
            ["Building_BeamManipulator.ReleaseInTransitThing"] = "Void(Thing):thing",
            ["BeamClaimUtility.ReleaseClaim"] = "Void(BeamTransfer,Int32):transfer,ownerKey",
            ["BeamClaimUtility.ReleaseAllClaimsForOwner"] = "Void(Map,Int32):map,ownerKey",
            ["BeamClaimUtility.TryClaimDestinationContainer"] = "Boolean(BeamTransfer,Int32):transfer,ownerKey"
        };
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        var provider = new Names();
        int count = 0;
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            if (reader.GetString(type.Namespace) != "ManipulatorBeam") continue;
            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                string key = reader.GetString(type.Name) + "." + reader.GetString(method.Name);
                if (!expected.TryGetValue(key, out string wanted)) continue;
                MethodSignature<string> signature = method.DecodeSignature(provider, (object)null);
                var names = new List<string>();
                foreach (ParameterHandle parameterHandle in method.GetParameters())
                {
                    Parameter parameter = reader.GetParameter(parameterHandle);
                    if (parameter.SequenceNumber == 0) continue;
                    names.Add(reader.GetString(parameter.Name));
                    bool byref = signature.ParameterTypes[parameter.SequenceNumber - 1].EndsWith("&", StringComparison.Ordinal);
                    if (byref != ((parameter.Attributes & ParameterAttributes.Out) != 0)) throw new Exception(key + " out/ref mismatch");
                }
                string actual = signature.ReturnType + "(" + string.Join(",", signature.ParameterTypes) + "):" + string.Join(",", names);
                bool shouldBeStatic = !key.EndsWith(".TryLiftForTransfer", StringComparison.Ordinal)
                    && !key.EndsWith(".ReleaseInTransitThing", StringComparison.Ordinal);
                if (actual != wanted || shouldBeStatic != ((method.Attributes & MethodAttributes.Static) != 0))
                    throw new Exception(key + " unexpected signature: " + actual);
                expected.Remove(key);
                count++;
            }
        }
        if (expected.Count != 0) throw new Exception("Missing DLL methods: " + string.Join(",", expected.Keys));
        Console.WriteLine("PASS: " + count + " actual DLL signatures (types, names, static/instance, out/ref).");
    }

    private sealed class Names : ISignatureTypeProvider<string, object>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> arguments) => genericType.Split('`')[0] + "<" + string.Join(",", arguments) + ">";
        public string GetGenericMethodParameter(object context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSpecification(MetadataReader reader, object context, TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    }
}
