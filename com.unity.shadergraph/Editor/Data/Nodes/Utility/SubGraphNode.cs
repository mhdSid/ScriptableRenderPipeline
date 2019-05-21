using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor.Graphing;

namespace UnityEditor.ShaderGraph
{
    [HasDependencies(typeof(MinimalSubGraphNode))]
    [Title("Utility", "Sub-graph")]
    class SubGraphNode : AbstractMaterialNode
        , IGeneratesBodyCode
        , IOnAssetEnabled
        , IGeneratesFunction
        , IMayRequireNormal
        , IMayRequireTangent
        , IMayRequireBitangent
        , IMayRequireMeshUV
        , IMayRequireScreenPosition
        , IMayRequireViewDirection
        , IMayRequirePosition
        , IMayRequireVertexColor
        , IMayRequireTime
        , IMayRequireFaceSign
        , IMayRequireCameraOpaqueTexture
        , IMayRequireDepthTexture
    {
        [Serializable]
        public class MinimalSubGraphNode : IHasDependencies
        {
        [SerializeField]
            string m_SerializedSubGraph = string.Empty;
            
            public void GetSourceAssetDependencies(List<string> paths)
            {
                var assetReference = JsonUtility.FromJson<SubGraphAssetReference>(m_SerializedSubGraph);
                var guid = assetReference?.subGraph?.guid;
                if (guid != null)
        {
                    paths.Add(AssetDatabase.GUIDToAssetPath(guid));
                }
            }
        }

        [Serializable]
        class SubGraphAssetReference
        {
            public AssetReference subGraph = default;

            public override string ToString()
            {
                return $"subGraph={subGraph}";
            }
        }

        [Serializable]
        class AssetReference
            {
            public long fileID = default;
            public string guid = default;
            public int type = default;

            public override string ToString()
            {
                return $"fileID={fileID}, guid={guid}, type={type}";
            }
        }

        [SerializeField]
        private string m_SerializedSubGraph = string.Empty;

        [NonSerialized]
        SubGraphAsset m_SubGraph;

        [SerializeField]
        List<string> m_PropertyGuids = new List<string>();

        [SerializeField]
        List<int> m_PropertyIds = new List<int>();

        [Serializable]
        private class SubGraphHelper
        {
            public SubGraphAsset subGraph;
        }

        public string subGraphGuid
        {
            get
            {
                var assetReference = JsonUtility.FromJson<SubGraphAssetReference>(m_SerializedSubGraph);
                return assetReference?.subGraph?.guid;
            }
        }

        void LoadSubGraph()
        {
            if (m_SubGraph == null)
            {
                if (string.IsNullOrEmpty(m_SerializedSubGraph))
                {
                    return;
                }
                
                var graphGuid = subGraphGuid;
                var assetPath = AssetDatabase.GUIDToAssetPath(graphGuid);
                m_SubGraph = AssetDatabase.LoadAssetAtPath<SubGraphAsset>(assetPath);
                if (m_SubGraph == null)
                {
                    return;
                }
                
                name = m_SubGraph.name;
                    }
                }

        public SubGraphAsset asset
        {
            get
            {
                LoadSubGraph();
                return m_SubGraph;
            }
            set
            {
                if (asset == value)
                    return;

                var helper = new SubGraphHelper();
                helper.subGraph = value;
                m_SerializedSubGraph = EditorJsonUtility.ToJson(helper, true);
                m_SubGraph = null;
                UpdateSlots();

                Dirty(ModificationScope.Topological);
            }
        }

        public override bool hasPreview
        {
            get { return asset != null; }
        }

        public override PreviewMode previewMode
        {
            get
            {
                if (asset == null)
                    return PreviewMode.Preview2D;

                return PreviewMode.Preview3D;
            }
        }

        public SubGraphNode()
        {
            name = "Sub Graph";
        }

        public override bool allowedInSubGraph
        {
            get { return true; }
        }


        public void GenerateNodeCode(ShaderGenerator visitor, GraphContext graphContext, GenerationMode generationMode)
        {
            var sb = new ShaderStringBuilder();
            if (asset == null || hasError)
            {
                var outputSlots = new List<MaterialSlot>();
                GetOutputSlots(outputSlots);
                foreach (var slot in outputSlots)
                {
                    visitor.AddShaderChunk($"{NodeUtils.ConvertConcreteSlotValueTypeToString(precision, slot.concreteValueType)} {GetVariableNameForSlot(slot.id)} = {slot.GetDefaultValue(GenerationMode.ForReals)};");
                }
                
                return;
            }

            var inputVariableName = $"_{GetVariableNameForNode()}";
            
            GraphUtil.GenerateSurfaceInputTransferCode(sb, asset.requirements, asset.inputStructName, inputVariableName);
            
            visitor.AddShaderChunk(sb.ToString());

            foreach (var outSlot in asset.outputs)
                visitor.AddShaderChunk(string.Format("{0} {1};", NodeUtils.ConvertConcreteSlotValueTypeToString(precision, outSlot.concreteValueType), GetVariableNameForSlot(outSlot.id)));

            var arguments = new List<string>();
            foreach (var prop in asset.inputs)
            {
                var inSlotId = m_PropertyIds[m_PropertyGuids.IndexOf(prop.guid.ToString())];

                if (prop is TextureShaderProperty)
                    arguments.Add(string.Format("TEXTURE2D_ARGS({0}, sampler{0})", GetSlotValue(inSlotId, generationMode)));
                else if (prop is Texture2DArrayShaderProperty)
                    arguments.Add(string.Format("TEXTURE2D_ARRAY_ARGS({0}, sampler{0})", GetSlotValue(inSlotId, generationMode)));
                else if (prop is Texture3DShaderProperty)
                    arguments.Add(string.Format("TEXTURE3D_ARGS({0}, sampler{0})", GetSlotValue(inSlotId, generationMode)));
                else if (prop is CubemapShaderProperty)
                    arguments.Add(string.Format("TEXTURECUBE_ARGS({0}, sampler{0})", GetSlotValue(inSlotId, generationMode)));
                else
                    arguments.Add(GetSlotValue(inSlotId, generationMode));
            }

            // pass surface inputs through
            arguments.Add(inputVariableName);

            foreach (var outSlot in asset.outputs)
                arguments.Add(GetVariableNameForSlot(outSlot.id));

            visitor.AddShaderChunk(
                string.Format("{0}({1});"
                    , asset.functionName
                    , arguments.Aggregate((current, next) => string.Format("{0}, {1}", current, next))));
        }

        public void OnEnable()
        {
            UpdateSlots();
        }

        public void Reload(HashSet<string> changedSubGraphs)
        {
            if (changedSubGraphs.Contains(asset.assetGuid) || asset.descendents.Any(changedSubGraphs.Contains))
            {
                m_SubGraph = null;
                UpdateSlots();
                owner.ClearErrorsForNode(this);
                ValidateNode();
                Dirty(ModificationScope.Graph);
            }
        }

        public virtual void UpdateSlots()
        {
            var validNames = new List<int>();
            if (asset == null)
            {
                return;
            }

            var props = asset.inputs;
            foreach (var prop in props)
            {
                var propType = prop.propertyType;
                SlotValueType slotType;

                switch (propType)
                {
                    case PropertyType.Color:
                        slotType = SlotValueType.Vector4;
                        break;
                    case PropertyType.Texture2D:
                        slotType = SlotValueType.Texture2D;
                        break;
                    case PropertyType.Texture2DArray:
                        slotType = SlotValueType.Texture2DArray;
                        break;
                    case PropertyType.Texture3D:
                        slotType = SlotValueType.Texture3D;
                        break;
                    case PropertyType.Cubemap:
                        slotType = SlotValueType.Cubemap;
                        break;
                    case PropertyType.Gradient:
                        slotType = SlotValueType.Gradient;
                        break;
                    case PropertyType.Vector1:
                        slotType = SlotValueType.Vector1;
                        break;
                    case PropertyType.Vector2:
                        slotType = SlotValueType.Vector2;
                        break;
                    case PropertyType.Vector3:
                        slotType = SlotValueType.Vector3;
                        break;
                    case PropertyType.Vector4:
                        slotType = SlotValueType.Vector4;
                        break;
                    case PropertyType.Boolean:
                        slotType = SlotValueType.Boolean;
                        break;
                    case PropertyType.Matrix2:
                        slotType = SlotValueType.Matrix2;
                        break;
                    case PropertyType.Matrix3:
                        slotType = SlotValueType.Matrix3;
                        break;
                    case PropertyType.Matrix4:
                        slotType = SlotValueType.Matrix4;
                        break;
                    case PropertyType.SamplerState:
                        slotType = SlotValueType.SamplerState;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                var propertyString = prop.guid.ToString();
                var propertyIndex = m_PropertyGuids.IndexOf(propertyString);
                if (propertyIndex < 0)
                {
                    propertyIndex = m_PropertyGuids.Count;
                    m_PropertyGuids.Add(propertyString);
                    m_PropertyIds.Add(prop.guid.GetHashCode());
                }
                var id = m_PropertyIds[propertyIndex];
                MaterialSlot slot = MaterialSlot.CreateMaterialSlot(slotType, id, prop.displayName, prop.referenceName, SlotType.Input, prop.defaultValue, ShaderStageCapability.All);
                
                // copy default for texture for niceness
                if (slotType == SlotValueType.Texture2D && propType == PropertyType.Texture2D)
                {
                    var tSlot = slot as Texture2DInputMaterialSlot;
                    var tProp = prop as TextureShaderProperty;
                    if (tSlot != null && tProp != null)
                        tSlot.texture = tProp.value.texture;
                }
                // copy default for texture array for niceness
                else if (slotType == SlotValueType.Texture2DArray && propType == PropertyType.Texture2DArray)
                {
                    var tSlot = slot as Texture2DArrayInputMaterialSlot;
                    var tProp = prop as Texture2DArrayShaderProperty;
                    if (tSlot != null && tProp != null)
                        tSlot.textureArray = tProp.value.textureArray;
                }
                // copy default for texture 3d for niceness
                else if (slotType == SlotValueType.Texture3D && propType == PropertyType.Texture3D)
                {
                    var tSlot = slot as Texture3DInputMaterialSlot;
                    var tProp = prop as Texture3DShaderProperty;
                    if (tSlot != null && tProp != null)
                        tSlot.texture = tProp.value.texture;
                }
                // copy default for cubemap for niceness
                else if (slotType == SlotValueType.Cubemap && propType == PropertyType.Cubemap)
                {
                    var tSlot = slot as CubemapInputMaterialSlot;
                    var tProp = prop as CubemapShaderProperty;
                    if (tSlot != null && tProp != null)
                        tSlot.cubemap = tProp.value.cubemap;
                }
                // copy default for gradient for niceness
                else if (slotType == SlotValueType.Gradient && propType == PropertyType.Gradient)
                {
                    var tSlot = slot as GradientInputMaterialSlot;
                    var tProp = prop as GradientShaderProperty;
                    if (tSlot != null && tProp != null)
                        tSlot.value = tProp.value;
                }
                AddSlot(slot);
                validNames.Add(id);
            }

            var outputStage = asset.effectiveShaderStage;

            foreach (var slot in asset.outputs)
                {
                    AddSlot(MaterialSlot.CreateMaterialSlot(slot.valueType, slot.id, slot.RawDisplayName(), 
                        slot.shaderOutputName, SlotType.Output, Vector4.zero, outputStage));
                    validNames.Add(slot.id);
                }

            RemoveSlotsNameNotMatching(validNames, true);
        }

        void ValidateShaderStage()
        {
            if (asset != null)
        {
            List<MaterialSlot> slots = new List<MaterialSlot>();
            GetInputSlots(slots);
            GetOutputSlots(slots);

                var outputStage = asset.effectiveShaderStage;
                foreach (MaterialSlot slot in slots)
                    slot.stageCapability = outputStage;
            }
        }

        public override void ValidateNode()
        {
            base.ValidateNode();
            
            if (asset == null)
            {
                hasError = true;
                var assetGuid = subGraphGuid;
                var assetPath = string.IsNullOrEmpty(subGraphGuid) ? null : AssetDatabase.GUIDToAssetPath(assetGuid);
                if (string.IsNullOrEmpty(assetPath))
                {
                    owner.AddValidationError(tempId, $"Could not find Sub Graph asset with GUID {assetGuid}.");
                }
                else
                {
                    owner.AddValidationError(tempId, $"Could not load Sub Graph asset at \"{assetPath}\" with GUID {assetGuid}.");
                }
            }
            else if (asset.isRecursive || owner.isSubGraph && (asset.descendents.Contains(owner.assetGuid) || asset.assetGuid == owner.assetGuid))
            {
                hasError = true;
                owner.AddValidationError(tempId, $"Detected a recursion in Sub Graph asset at \"{AssetDatabase.GUIDToAssetPath(subGraphGuid)}\" with GUID {subGraphGuid}.");
            }
            else if (!asset.isValid)
            {
                hasError = true;
                owner.AddValidationError(tempId, $"Invalid Sub Graph asset at \"{AssetDatabase.GUIDToAssetPath(subGraphGuid)}\" with GUID {subGraphGuid}.");
            }

            ValidateShaderStage();
        }

        public override void CollectShaderProperties(PropertyCollector visitor, GenerationMode generationMode)
        {
            base.CollectShaderProperties(visitor, generationMode);

            if (asset == null)
                return;

            foreach (var property in asset.nodeProperties)
            {
                visitor.AddShaderProperty(property);
            }
        }

        public override void CollectPreviewMaterialProperties(List<PreviewProperty> properties)
        {
            base.CollectPreviewMaterialProperties(properties);
            
            if (asset == null)
                return;

            foreach (var property in asset.nodeProperties)
            {
                properties.Add(property.GetPreviewMaterialProperty());
        }
        }

        public virtual void GenerateNodeFunction(FunctionRegistry registry, GraphContext graphContext, GenerationMode generationMode)
        {
            if (asset == null || hasError)
                return;

            foreach (var function in asset.functions)
            {
                registry.ProvideFunction(function.key, s =>
                {
                    s.AppendLines(function.value);
                });
            }
        }

        public NeededCoordinateSpace RequiresNormal(ShaderStageCapability stageCapability)
        {
            if (asset == null)
                return NeededCoordinateSpace.None;

            return asset.requirements.requiresNormal;
        }

        public bool RequiresMeshUV(UVChannel channel, ShaderStageCapability stageCapability)
        {
            if (asset == null)
                return false;

            return asset.requirements.requiresMeshUVs.Contains(channel);
        }

        public bool RequiresScreenPosition(ShaderStageCapability stageCapability)
        {
            if (asset == null)
                return false;

            return asset.requirements.requiresScreenPosition;
        }

        public NeededCoordinateSpace RequiresViewDirection(ShaderStageCapability stageCapability)
        {
            if (asset == null)
                return NeededCoordinateSpace.None;

            return asset.requirements.requiresViewDir;
        }

        public NeededCoordinateSpace RequiresPosition(ShaderStageCapability stageCapability)
        {
            if (asset == null)
                return NeededCoordinateSpace.None;

            return asset.requirements.requiresPosition;
        }

        public NeededCoordinateSpace RequiresTangent(ShaderStageCapability stageCapability)
        {
            if (asset == null)
                return NeededCoordinateSpace.None;

            return asset.requirements.requiresTangent;
        }

        public bool RequiresTime()
        {
            if (asset == null)
                return false;

            return asset.requirements.requiresTime;
        }

        public bool RequiresFaceSign(ShaderStageCapability stageCapability)
        {
            if (asset == null)
                return false;

            return asset.requirements.requiresFaceSign;
        }

        public NeededCoordinateSpace RequiresBitangent(ShaderStageCapability stageCapability)
        {
            if (asset == null)
                return NeededCoordinateSpace.None;

            return asset.requirements.requiresBitangent;
        }

        public bool RequiresVertexColor(ShaderStageCapability stageCapability)
        {
            if (asset == null)
                return false;

            return asset.requirements.requiresVertexColor;
        }

        public bool RequiresCameraOpaqueTexture(ShaderStageCapability stageCapability)
        {
            if (asset == null)
                return false;

            return asset.requirements.requiresCameraOpaqueTexture;
        }

        public bool RequiresDepthTexture(ShaderStageCapability stageCapability)
        {
            if (asset == null)
                return false;

            return asset.requirements.requiresDepthTexture;
        }
    }
}
