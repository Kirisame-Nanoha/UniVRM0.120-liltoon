using System;
using System.Collections.Generic;
using System.Linq;
using UniGLTF;
using UnityEngine;
using VRMShaders;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VRM
{
    public static class BuiltInVrmMToonMaterialImporter
    {
        /// <summary>
        /// 過去バージョンに含まれていたが、廃止・統合された Shader のフォールバック情報
        /// </summary>
        public static readonly Dictionary<string, string> FallbackShaders = new Dictionary<string, string>
        {
            {"VRM/UnlitTexture", "Unlit/Texture"},
            {"VRM/UnlitTransparent", "Unlit/Transparent"},
            {"VRM/UnlitCutout", "Unlit/Transparent Cutout"},
            {"UniGLTF/StandardVColor", UniGLTF.UniUnlit.UniUnlitUtil.ShaderName},
        };

        private static bool LooksLikeColorPropertyName(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (key.IndexOf("tint", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (key == "_Color") return true;
            return false;
        }

        private static bool LooksLikeTexturePropertyName(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            // かなり雑だが、最後の砦。Texture 系の命名に寄せる
            if (key.EndsWith("Tex", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.EndsWith("Texture", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.EndsWith("Map", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.EndsWith("Mask", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (key.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private static bool TryGetShaderPropertyType(Shader shader, string key, out int propertyType)
        {
            propertyType = -1;
            if (shader == null || string.IsNullOrEmpty(key))
            {
                return false;
            }

#if UNITY_EDITOR
            try
            {
                var count = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < count; ++i)
                {
                    if (ShaderUtil.GetPropertyName(shader, i) != key)
                    {
                        continue;
                    }

                    // ShaderUtil.ShaderPropertyType:
                    // Color=0, Vector=1, Float=2, Range=3, TexEnv=4
                    propertyType = (int)ShaderUtil.GetPropertyType(shader, i);
                    return true;
                }
            }
            catch
            {
                // ignore -> runtime api / heuristicへ
            }
#endif

#if UNITY_2019_1_OR_NEWER
            try
            {
                var count = shader.GetPropertyCount();
                for (int i = 0; i < count; ++i)
                {
                    if (shader.GetPropertyName(i) != key)
                    {
                        continue;
                    }

                    // UnityEngine.Rendering.ShaderPropertyType
                    // Color, Vector, Float, Range, Texture
                    var t = shader.GetPropertyType(i);
                    // こちらは enum なので int 化して返す。Texture を 4 相当として扱う
                    // (Editor の ShaderUtil と揃えるため)
                    switch (t)
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Color:
                            propertyType = 0;
                            return true;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector:
                            propertyType = 1;
                            return true;
                        case UnityEngine.Rendering.ShaderPropertyType.Float:
                            propertyType = 2;
                            return true;
                        case UnityEngine.Rendering.ShaderPropertyType.Range:
                            propertyType = 3;
                            return true;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture:
                            propertyType = 4;
                            return true;
                        default:
                            propertyType = -1;
                            return true;
                    }
                }
            }
            catch
            {
                // ignore -> heuristicへ
            }
#endif

            return false;
        }

        /// <summary>
        /// key が shader 上で Texture 型プロパティなら true
        /// </summary>
        private static bool IsTextureProperty(Shader shader, string key)
        {
            if (TryGetShaderPropertyType(shader, key, out var t))
            {
                // ShaderUtil/このラッパーでは Texture を 4 として扱う
                return t == 4;
            }

            // 型が取れない場合のフォールバック
            return LooksLikeTexturePropertyName(key);
        }

        private static bool TryAsColor(Shader shader, string key, float[] raw, out Color color)
        {
            color = default;

            if (raw == null || raw.Length < 4)
            {
                return false;
            }

#if UNITY_EDITOR
            // Editor では shader のプロパティ型が取れるので Color を優先
            if (shader != null)
            {
                try
                {
                    var count = ShaderUtil.GetPropertyCount(shader);
                    for (int i = 0; i < count; ++i)
                    {
                        if (ShaderUtil.GetPropertyName(shader, i) != key)
                        {
                            continue;
                        }

                        if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.Color)
                        {
                            color = new Color(raw[0], raw[1], raw[2], raw[3]);
                            return true;
                        }

                        // 同名が見つかったが Color ではない
                        return false;
                    }
                }
                catch
                {
                    // Unity の内部 API が失敗した場合はヒューリスティックへ
                }
            }
#endif

            // Runtime / fallback: 名前から推測
            if (LooksLikeColorPropertyName(key))
            {
                color = new Color(raw[0], raw[1], raw[2], raw[3]);
                return true;
            }

            return false;
        }

        public static bool TryCreateParam(GltfData data, glTF_VRM_extensions vrm, int materialIdx,
            out MaterialDescriptor matDesc)
        {
            if (vrm?.materialProperties == null || vrm.materialProperties.Count == 0)
            {
                matDesc = default;
                return false;
            }

            if (materialIdx < 0 || materialIdx >= vrm.materialProperties.Count)
            {
                matDesc = default;
                return false;
            }

            var vrmMaterial = vrm.materialProperties[materialIdx];
            if (vrmMaterial.shader == glTF_VRM_Material.VRM_USE_GLTFSHADER)
            {
                // fallback to gltf
                matDesc = default;
                return false;
            }

            //
            // restore VRM material (任意 shader を許容)
            //
            // use material.name, because material name may renamed in GltfParser.
            var name = data.GLTF.materials[materialIdx].name;
            var shaderName = vrmMaterial.shader;
            if (FallbackShaders.ContainsKey(shaderName))
            {
                shaderName = FallbackShaders[shaderName];
            }
            var shader = Shader.Find(shaderName);

            var textureSlots = new Dictionary<string, TextureDescriptor>();
            var floatValues = new Dictionary<string, float>();
            var colors = new Dictionary<string, Color>();
            var vectors = new Dictionary<string, Vector4>();
            var actions = new List<Action<Material>>();
            matDesc = new MaterialDescriptor(
                name,
                shader,
                vrmMaterial.renderQueue,
                textureSlots,
                floatValues,
                colors,
                vectors,
                actions);

            foreach (var kv in vrmMaterial.floatProperties)
            {
                floatValues[kv.Key] = kv.Value;
            }

            // textureProperties のキー集合（後段の TryGetTexture... 成否に依らず、型衝突回避用に持っておく）
            var textureKeys = new HashSet<string>();
            if (vrmMaterial.textureProperties != null)
            {
                foreach (var k in vrmMaterial.textureProperties.Keys)
                {
                    textureKeys.Add(k);
                }
            }

            // vectorProperties には「色/ベクター」だけでなく「テクスチャの offset&scale」も入っている
            // 既存実装は MToon のテクスチャスロット名で除外していたが、
            // lilToon など他 shader ではスロット名が一致しないため誤って SetVector されてしまう。
            //
            // ここでは
            // - textureProperties に存在する key
            // - shader 上で Texture 型の key
            // を Vector/Color 復元から除外する（SetVector 衝突防止）。
            foreach (var kv in vrmMaterial.vectorProperties)
            {
                if (kv.Value == null || kv.Value.Length < 4)
                {
                    continue;
                }

                // texture offset&scale は TextureDescriptor 経由で MaterialFactory が適用する
                if (textureKeys.Contains(kv.Key))
                {
                    continue;
                }

                // ★重要：shader 上で Texture 型なら SetVector しない（今回のエラー対策）
                if (IsTextureProperty(shader, kv.Key))
                {
                    continue;
                }

                // 型が取れる場合は Vector/Color 以外も弾いて安全にする
                if (TryGetShaderPropertyType(shader, kv.Key, out var t))
                {
                    // Color(0) / Vector(1) 以外は vectors/colors に入れない
                    if (t != 0 && t != 1)
                    {
                        continue;
                    }
                }

                if (TryAsColor(shader, kv.Key, kv.Value, out var c))
                {
                    colors[kv.Key] = c;
                }
                else
                {
                    var v = new Vector4(kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3]);
                    vectors[kv.Key] = v;
                }
            }

            foreach (var kv in vrmMaterial.textureProperties)
            {
                if (VRMMToonTextureImporter.TryGetTextureFromMaterialProperty(data, vrmMaterial, kv.Key,
                    out var key, out var desc))
                {
                    textureSlots[kv.Key] = desc;
                }
            }

            foreach (var kv in vrmMaterial.keywordMap)
            {
                if (kv.Value)
                {
                    actions.Add(material => material.EnableKeyword(kv.Key));
                }
                else
                {
                    actions.Add(material => material.DisableKeyword(kv.Key));
                }
            }

            foreach (var kv in vrmMaterial.tagMap)
            {
                actions.Add(material => material.SetOverrideTag(kv.Key, kv.Value));
            }

            if (vrmMaterial.shader == MToon.Utils.ShaderName)
            {
                // TODO: Material拡張にMToonの項目が追加されたら旧バージョンのshaderPropから変換をかける
                // インポート時にUniVRMに含まれるMToonのバージョンに上書きする
                floatValues[MToon.Utils.PropVersion] = MToon.Utils.VersionNumber;
            }

            return true;
        }
    }
}
