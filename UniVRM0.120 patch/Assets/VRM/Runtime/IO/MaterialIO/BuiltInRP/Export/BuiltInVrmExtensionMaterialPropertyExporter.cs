using System;
using UniGLTF;
using UniGLTF.ShaderPropExporter;
using UnityEngine;
using VRMShaders;
using ColorSpace = VRMShaders.ColorSpace;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VRM
{
    /// <summary>
    /// VRM extension 内の materialProperties に記録するデータを用意する。
    ///
    /// UniVRM 0.120.0 の標準実装は、VRM/MToon 以外の shader を VRM_USE_GLTFSHADER 扱いにして
    /// VRM.materialProperties.shader を "VRM_USE_GLTFSHADER" に書き換える。
    ///
    /// その結果、lilToon など独自 shader の情報が VRM 内に残らず、
    /// UniVRM で再インポートしたときに「元の shader のまま復元」できない。
    ///
    /// ここでは lilToon の場合だけ、shader 名とプロパティ一式を VRM.materialProperties に保存して
    /// UniVRM の importer 側が lilToon を復元できるようにする。
    ///
    /// 注意:
    /// - VRM0 仕様上、ビューアは任意 shader を解釈しない。Unity(UniVRM + lilToon導入済み) の再インポート用途を想定。
    /// - UNITY_EDITOR が無い環境(ランタイム export)では shader プロパティ列挙ができないため、従来通り VRM_USE_GLTFSHADER にフォールバックする。
    /// </summary>
    public static class BuiltInVrmExtensionMaterialPropertyExporter
    {
        private static readonly string[] ExportingTags =
        {
            "RenderType",
            // "Queue",
        };

        /// <summary>
        /// Dictionary.Add の重複キー例外を避けて上書きする
        /// (lilToon の ShaderProps には重複名プロパティが混ざることがある)
        /// </summary>
        private static void SetOrReplace<TKey, TValue>(System.Collections.Generic.Dictionary<TKey, TValue> dict, TKey key, TValue value)
        {
            if (dict.ContainsKey(key))
            {
                dict[key] = value;
            }
            else
            {
                dict.Add(key, value);
            }
        }

        private static bool IsLilToonShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            return shaderName.IndexOf("liltoon", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsLikelyNormalMapProperty(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return false;

            // lilToon / 一般的な命名
            if (propertyName == "_BumpMap") return true;
            if (propertyName.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (propertyName.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private static bool NeedsAlphaForSrgbTexture(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return false;

            // 基本となる色テクスチャは alpha を持つことが多い
            // (lilToon はマスク用途も多いので広めに拾う)
            if (propertyName == "_MainTex") return true;
            if (propertyName.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (propertyName.IndexOf("base", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (propertyName.IndexOf("albedo", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (propertyName.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (propertyName.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        public static glTF_VRM_Material ExportMaterial(Material m, ITextureExporter textureExporter)
        {
            var material = new glTF_VRM_Material
            {
                name = m.name,
                shader = m.shader.name,
                renderQueue = m.renderQueue,
            };

            // MToon は従来通り(仕様準拠)
            if (m.shader.name == MToon.Utils.ShaderName)
            {
                var prop = PreShaderPropExporter.GetPropsForMToon();
                if (prop == null)
                {
                    throw new Exception("MToon property list not found.");
                }

                foreach (var keyword in m.shaderKeywords)
                {
                    SetOrReplace(material.keywordMap, keyword, m.IsKeywordEnabled(keyword));
                }

                foreach (var kv in prop.Properties)
                {
                    switch (kv.ShaderPropertyType)
                    {
                        case ShaderPropertyType.Color:
                            {
                                // No color conversion. Because color property is serialized to raw float array.
                                var value = m.GetColor(kv.Key).ToFloat4(ColorSpace.Linear, ColorSpace.Linear);
                                SetOrReplace(material.vectorProperties, kv.Key, value);
                            }
                            break;

                        case ShaderPropertyType.Range:
                        case ShaderPropertyType.Float:
                            {
                                var value = m.GetFloat(kv.Key);
                                SetOrReplace(material.floatProperties, kv.Key, value);
                            }
                            break;

                        case ShaderPropertyType.TexEnv:
                            {
                                var texture = m.GetTexture(kv.Key);
                                if (texture != null)
                                {
                                    var value = -1;
                                    var isNormalMap = kv.Key == "_BumpMap";
                                    if (isNormalMap)
                                    {
                                        value = textureExporter.RegisterExportingAsNormal(texture);
                                    }
                                    else
                                    {
                                        var needsAlpha = kv.Key == "_MainTex";
                                        value = textureExporter.RegisterExportingAsSRgb(texture, needsAlpha);
                                    }

                                    if (value == -1)
                                    {
                                        Debug.LogFormat("not found {0}", texture.name);
                                    }
                                    else
                                    {
                                        SetOrReplace(material.textureProperties, kv.Key, value);
                                    }
                                }

                                // offset & scaling
                                var offset = m.GetTextureOffset(kv.Key);
                                var scaling = m.GetTextureScale(kv.Key);
                                SetOrReplace(material.vectorProperties, kv.Key,
                                    new float[] { offset.x, offset.y, scaling.x, scaling.y });
                            }
                            break;

                        case ShaderPropertyType.Vector:
                            {
                                var value = m.GetVector(kv.Key).ToArray();
                                SetOrReplace(material.vectorProperties, kv.Key, value);
                            }
                            break;

                        default:
                            throw new NotImplementedException();
                    }
                }

                foreach (var tag in ExportingTags)
                {
                    var value = m.GetTag(tag, false);
                    if (!string.IsNullOrEmpty(value))
                    {
                        SetOrReplace(material.tagMap, tag, value);
                    }
                }

                return material;
            }

            // lilToon の場合だけ、shader 名とプロパティ一式を VRM.materialProperties に残す
            if (IsLilToonShader(m.shader.name))
            {
#if UNITY_EDITOR
                // ShaderProps.FromShader は UNITY_EDITOR 時のみ有効
                var prop = ShaderProps.FromShader(m.shader);
                if (prop == null || prop.Properties == null)
                {
                    // 念のためのフォールバック
                    material.shader = glTF_VRM_Material.VRM_USE_GLTFSHADER;
                    return material;
                }

                foreach (var keyword in m.shaderKeywords)
                {
                    SetOrReplace(material.keywordMap, keyword, m.IsKeywordEnabled(keyword));
                }

                foreach (var kv in prop.Properties)
                {
                    // shader の property と material の property が一致しないケース(variant 等)を避ける
                    if (!m.HasProperty(kv.Key))
                    {
                        continue;
                    }

                    switch (kv.ShaderPropertyType)
                    {
                        case ShaderPropertyType.Color:
                            {
                                var value = m.GetColor(kv.Key).ToFloat4(ColorSpace.Linear, ColorSpace.Linear);
                                SetOrReplace(material.vectorProperties, kv.Key, value);
                            }
                            break;

                        case ShaderPropertyType.Range:
                        case ShaderPropertyType.Float:
                            {
                                SetOrReplace(material.floatProperties, kv.Key, m.GetFloat(kv.Key));
                            }
                            break;

                        case ShaderPropertyType.Vector:
                            {
                                SetOrReplace(material.vectorProperties, kv.Key, m.GetVector(kv.Key).ToArray());
                            }
                            break;

                        case ShaderPropertyType.TexEnv:
                            {
                                var texture = m.GetTexture(kv.Key);
                                if (texture != null)
                                {
                                    var texIndex = -1;
                                    if (IsLikelyNormalMapProperty(kv.Key))
                                    {
                                        texIndex = textureExporter.RegisterExportingAsNormal(texture);
                                    }
                                    else
                                    {
                                        var needsAlpha = NeedsAlphaForSrgbTexture(kv.Key);
                                        texIndex = textureExporter.RegisterExportingAsSRgb(texture, needsAlpha);
                                    }

                                    if (texIndex == -1)
                                    {
                                        Debug.LogFormat("not found {0}", texture.name);
                                    }
                                    else
                                    {
                                        SetOrReplace(material.textureProperties, kv.Key, texIndex);
                                    }
                                }

                                // offset & scaling を vectorProperties に保持(Importer 側で TextureDescriptor に反映)
                                var offset = m.GetTextureOffset(kv.Key);
                                var scaling = m.GetTextureScale(kv.Key);
                                SetOrReplace(material.vectorProperties, kv.Key, new float[] { offset.x, offset.y, scaling.x, scaling.y });
                            }
                            break;

                        default:
                            throw new NotImplementedException();
                    }
                }

                foreach (var tag in ExportingTags)
                {
                    var value = m.GetTag(tag, false);
                    if (!string.IsNullOrEmpty(value))
                    {
                        SetOrReplace(material.tagMap, tag, value);
                    }
                }

                // ここでは VRM_USE_GLTFSHADER にしない(= shader 名を保持)
                return material;
#else
                // ランタイム export では shader のプロパティ列挙ができないため、安全側に倒す
                material.shader = glTF_VRM_Material.VRM_USE_GLTFSHADER;
                return material;
#endif
            }

            // その他の shader は従来通り glTF 側に委譲する
            material.shader = glTF_VRM_Material.VRM_USE_GLTFSHADER;
            return material;
        }
    }
}
