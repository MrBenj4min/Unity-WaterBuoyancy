using System;
using System.Collections.Generic;
using UnityEngine;

namespace WaterBuoyancy
{
    [ExecuteInEditMode] // Make water live-update even when not in play mode
    public class WaterFX : MonoBehaviour
    {
        public enum WaterMode
        {
            Simple = 0,
            Reflective = 1,
            Refractive = 2,
        };

        [SerializeField]
        private WaterMode waterMode = WaterMode.Refractive;

        [SerializeField]
        private bool disablePixelLights = true;

        [SerializeField]
        private int textureSize = 256;

        [SerializeField]
        private float clipPlaneOffset = 0.07f;

        [SerializeField]
        private LayerMask reflectLayers = -1;

        [SerializeField]
        private LayerMask refractLayers = -1;

        private Dictionary<Camera, Camera> m_ReflectionCameras = new Dictionary<Camera, Camera>(); // Camera -> Camera table
        private Dictionary<Camera, Camera> m_RefractionCameras = new Dictionary<Camera, Camera>(); // Camera -> Camera table
        private RenderTexture m_ReflectionTexture;
        private RenderTexture m_RefractionTexture;
        private WaterMode m_HardwareWaterSupport = WaterMode.Refractive;
        private int m_OldReflectionTextureSize;
        private int m_OldRefractionTextureSize;
        private static bool s_InsideWater;

        private Renderer m_renderer;
        private WaterMode m_mode;

        private void Awake()
        {
            m_renderer = GetComponent<Renderer>();
            if (m_renderer == null)
                enabled = false;

            m_HardwareWaterSupport = FindHardwareWaterSupport();

            // Optionally disable pixel lights for reflection/refraction
            int oldPixelLightCount = QualitySettings.pixelLightCount;
            if (disablePixelLights)
            {
                QualitySettings.pixelLightCount = 0;
            }

            m_mode = GetWaterMode();
            // Setup shader keywords based on water mode
            switch (m_mode)
            {
                case WaterMode.Simple:
                    Shader.EnableKeyword("WATER_SIMPLE");
                    Shader.DisableKeyword("WATER_REFLECTIVE");
                    Shader.DisableKeyword("WATER_REFRACTIVE");
                    break;
                case WaterMode.Reflective:
                    Shader.DisableKeyword("WATER_SIMPLE");
                    Shader.EnableKeyword("WATER_REFLECTIVE");
                    Shader.DisableKeyword("WATER_REFRACTIVE");
                    break;
                case WaterMode.Refractive:
                    Shader.DisableKeyword("WATER_SIMPLE");
                    Shader.DisableKeyword("WATER_REFLECTIVE");
                    Shader.EnableKeyword("WATER_REFRACTIVE");
                    break;
            }

        }

        Camera m_reflectionCamera, m_refractionCamera;
        Vector4 reflectionPlane = Vector4.zero;

        // This is called when it's known that the object will be rendered by some
        // camera. We render reflections / refractions and do other updates here.
        // Because the script executes in edit mode, reflections for the scene view
        // camera will just work!
        public void OnWillRenderObject()
        {
            Camera cam = Camera.current;
            if (!cam)
            {
                return;
            }

            // Safeguard from recursive water reflections.
            if (s_InsideWater)
            {
                return;
            }
            s_InsideWater = true;

            if (!m_reflectionCamera)
            {
                CreateWaterObjects(cam, out m_reflectionCamera, out m_refractionCamera);
                UpdateCameraModes(cam, m_reflectionCamera);
                UpdateCameraModes(cam, m_refractionCamera);
            }

            // find out the reflection plane: position and normal in world space
            Vector3 pos = transform.position;
            Vector3 normal = transform.up;

            // Render reflection if needed
            if (m_mode >= WaterMode.Reflective)
            {
                // Reflect camera around reflection plane
                float d = -Vector3.Dot(normal, pos) - clipPlaneOffset;
                reflectionPlane.Set(normal.x, normal.y, normal.z, d);

                Matrix4x4 reflection = Matrix4x4.zero;
                CalculateReflectionMatrix(ref reflection, reflectionPlane);
                Vector3 oldpos = cam.transform.position;
                Vector3 newpos = reflection.MultiplyPoint(oldpos);
                m_reflectionCamera.worldToCameraMatrix = cam.worldToCameraMatrix * reflection;

                // Setup oblique projection matrix so that near plane is our reflection
                // plane. This way we clip everything below/above it for free.
                Vector4 clipPlane = CameraSpacePlane(m_reflectionCamera, pos, normal, 1.0f);
                m_reflectionCamera.projectionMatrix = cam.CalculateObliqueMatrix(clipPlane);

                m_reflectionCamera.cullingMask = ~(1 << 4) & reflectLayers.value; // never render water layer
                //reflectionCamera.targetTexture = m_ReflectionTexture;
                GL.invertCulling = true;
                m_reflectionCamera.transform.position = newpos;
                Vector3 euler = cam.transform.eulerAngles;
                m_reflectionCamera.transform.eulerAngles.Set(-euler.x, euler.y, euler.z);
                m_reflectionCamera.Render();
                m_reflectionCamera.transform.position = oldpos;
                GL.invertCulling = false;
                m_renderer.sharedMaterial.SetTexture("_ReflectionTex", m_ReflectionTexture);
            }

            // Render refraction
            if (m_mode >= WaterMode.Refractive)
            {
                m_refractionCamera.worldToCameraMatrix = cam.worldToCameraMatrix;

                // Setup oblique projection matrix so that near plane is our reflection
                // plane. This way we clip everything below/above it for free.
                Vector4 clipPlane = CameraSpacePlane(m_refractionCamera, pos, normal, -1.0f);
                m_refractionCamera.projectionMatrix = cam.CalculateObliqueMatrix(clipPlane);

                m_refractionCamera.cullingMask = ~(1 << 4) & refractLayers.value; // never render water layer
                m_refractionCamera.transform.position = cam.transform.position;
                m_refractionCamera.transform.rotation = cam.transform.rotation;
                m_refractionCamera.Render();
                m_renderer.sharedMaterial.SetTexture("_RefractionTex", m_RefractionTexture);
            }

            s_InsideWater = false;
        }


        // Cleanup all the objects we possibly have created
        void OnDisable()
        {
            if (m_ReflectionTexture)
            {
                DestroyImmediate(m_ReflectionTexture);
                m_ReflectionTexture = null;
            }
            if (m_RefractionTexture)
            {
                DestroyImmediate(m_RefractionTexture);
                m_RefractionTexture = null;
            }
            foreach (var kvp in m_ReflectionCameras)
            {
                DestroyImmediate((kvp.Value).gameObject);
            }
            m_ReflectionCameras.Clear();
            foreach (var kvp in m_RefractionCameras)
            {
                DestroyImmediate((kvp.Value).gameObject);
            }
            m_RefractionCameras.Clear();
        }


        Vector4 m_waveScale4 = Vector4.zero;

        // This just sets up some matrices in the material; for really
        // old cards to make water texture scroll.
        void Update()
        {
            if (!m_renderer)
            {
                return;
            }
            Material mat = m_renderer.sharedMaterial;
            if (!mat)
            {
                return;
            }

            Vector4 waveSpeed = mat.GetVector("WaveSpeed");
            float waveScale = mat.GetFloat("_WaveScale");
            m_waveScale4.Set(waveScale, waveScale, waveScale * 0.4f, waveScale * 0.45f);

            // Time since level load, and do intermediate calculations with doubles
            double t = Time.timeSinceLevelLoad / 20.0;
            Vector4 offsetClamped = new Vector4(
                (float)Math.IEEERemainder(waveSpeed.x * m_waveScale4.x * t, 1.0),
                (float)Math.IEEERemainder(waveSpeed.y * m_waveScale4.y * t, 1.0),
                (float)Math.IEEERemainder(waveSpeed.z * m_waveScale4.z * t, 1.0),
                (float)Math.IEEERemainder(waveSpeed.w * m_waveScale4.w * t, 1.0)
                );

            mat.SetVector("_WaveOffset", offsetClamped);
            mat.SetVector("_WaveScale4", m_waveScale4);
        }

        void UpdateCameraModes(Camera src, Camera dest)
        {
            if (dest == null)
            {
                return;
            }
            // set water camera to clear the same way as current camera
            dest.clearFlags = src.clearFlags;
            dest.backgroundColor = src.backgroundColor;
            if (src.clearFlags == CameraClearFlags.Skybox)
            {
                Skybox sky = src.GetComponent<Skybox>();
                Skybox mysky = dest.GetComponent<Skybox>();
                if (!sky || !sky.material)
                {
                    mysky.enabled = false;
                }
                else
                {
                    mysky.enabled = true;
                    mysky.material = sky.material;
                }
            }
            // update other values to match current camera.
            // even if we are supplying custom camera&projection matrices,
            // some of values are used elsewhere (e.g. skybox uses far plane)
            dest.farClipPlane = src.farClipPlane;
            dest.nearClipPlane = src.nearClipPlane;
            dest.orthographic = src.orthographic;
            dest.fieldOfView = src.fieldOfView;
            dest.aspect = src.aspect;
            dest.orthographicSize = src.orthographicSize;
        }


        // On-demand create any objects we need for water
        void CreateWaterObjects(Camera currentCamera, out Camera reflectionCamera, out Camera refractionCamera)
        {
            WaterMode mode = GetWaterMode();

            reflectionCamera = null;
            refractionCamera = null;

            if (mode >= WaterMode.Reflective)
            {
                // Reflection render texture
                if (!m_ReflectionTexture || m_OldReflectionTextureSize != textureSize)
                {
                    if (m_ReflectionTexture)
                    {
                        DestroyImmediate(m_ReflectionTexture);
                    }
                    m_ReflectionTexture = new RenderTexture(textureSize, textureSize, 16);
                    m_ReflectionTexture.name = "__WaterReflection" + GetInstanceID();
                    m_ReflectionTexture.isPowerOfTwo = true;
                    m_ReflectionTexture.hideFlags = HideFlags.DontSave;
                    m_OldReflectionTextureSize = textureSize;
                }

                // Camera for reflection
                m_ReflectionCameras.TryGetValue(currentCamera, out reflectionCamera);
                if (!reflectionCamera) // catch both not-in-dictionary and in-dictionary-but-deleted-GO
                {
                    GameObject go = new GameObject("Water Refl Camera id" + GetInstanceID() + " for " + currentCamera.GetInstanceID(), typeof(Camera), typeof(Skybox));
                    reflectionCamera = go.GetComponent<Camera>();
                    reflectionCamera.enabled = false;
                    reflectionCamera.transform.position = transform.position;
                    reflectionCamera.transform.rotation = transform.rotation;
                    reflectionCamera.targetTexture = m_ReflectionTexture;
                    reflectionCamera.gameObject.AddComponent<FlareLayer>();
                    go.hideFlags = HideFlags.HideAndDontSave;
                    m_ReflectionCameras[currentCamera] = reflectionCamera;
                }
            }

            if (mode >= WaterMode.Refractive)
            {
                // Refraction render texture
                if (!m_RefractionTexture || m_OldRefractionTextureSize != textureSize)
                {
                    if (m_RefractionTexture)
                    {
                        DestroyImmediate(m_RefractionTexture);
                    }
                    m_RefractionTexture = new RenderTexture(textureSize, textureSize, 16);
                    m_RefractionTexture.name = "__WaterRefraction" + GetInstanceID();
                    m_RefractionTexture.isPowerOfTwo = true;
                    m_RefractionTexture.hideFlags = HideFlags.DontSave;
                    m_OldRefractionTextureSize = textureSize;
                }

                // Camera for refraction
                m_RefractionCameras.TryGetValue(currentCamera, out refractionCamera);
                if (!refractionCamera) // catch both not-in-dictionary and in-dictionary-but-deleted-GO
                {
                    GameObject go =
                        new GameObject("Water Refr Camera id" + GetInstanceID() + " for " + currentCamera.GetInstanceID(),
                            typeof(Camera), typeof(Skybox));
                    refractionCamera = go.GetComponent<Camera>();
                    refractionCamera.enabled = false;
                    refractionCamera.transform.position = transform.position;
                    refractionCamera.transform.rotation = transform.rotation;
                    refractionCamera.targetTexture = m_RefractionTexture;
                    refractionCamera.gameObject.AddComponent<FlareLayer>();
                    go.hideFlags = HideFlags.HideAndDontSave;
                    m_RefractionCameras[currentCamera] = refractionCamera;
                }
            }
        }

        WaterMode GetWaterMode()
        {
            if (m_HardwareWaterSupport < waterMode)
            {
                return m_HardwareWaterSupport;
            }
            return waterMode;
        }

        WaterMode FindHardwareWaterSupport()
        {
            Material mat = m_renderer.sharedMaterial;
            if (!mat)
            {
                return WaterMode.Simple;
            }

            string mode = mat.GetTag("WATERMODE", false);
            if (mode == "Refractive")
            {
                return WaterMode.Refractive;
            }
            if (mode == "Reflective")
            {
                return WaterMode.Reflective;
            }

            return WaterMode.Simple;
        }

        // Given position/normal of the plane, calculates plane in camera space.
        Vector4 CameraSpacePlane(Camera cam, Vector3 pos, Vector3 normal, float sideSign)
        {
            Vector4 plane = Vector4.zero;
            Vector3 offsetPos = pos + normal * clipPlaneOffset;
            Matrix4x4 m = cam.worldToCameraMatrix;
            Vector3 cpos = m.MultiplyPoint(offsetPos);
            Vector3 cnormal = m.MultiplyVector(normal).normalized * sideSign;
            plane.Set(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
            return plane;
        }

        // Calculates reflection matrix around the given plane
        static void CalculateReflectionMatrix(ref Matrix4x4 reflectionMat, Vector4 plane)
        {
            reflectionMat.m00 = (1F - 2F * plane[0] * plane[0]);
            reflectionMat.m01 = (-2F * plane[0] * plane[1]);
            reflectionMat.m02 = (-2F * plane[0] * plane[2]);
            reflectionMat.m03 = (-2F * plane[3] * plane[0]);

            reflectionMat.m10 = (-2F * plane[1] * plane[0]);
            reflectionMat.m11 = (1F - 2F * plane[1] * plane[1]);
            reflectionMat.m12 = (-2F * plane[1] * plane[2]);
            reflectionMat.m13 = (-2F * plane[3] * plane[1]);

            reflectionMat.m20 = (-2F * plane[2] * plane[0]);
            reflectionMat.m21 = (-2F * plane[2] * plane[1]);
            reflectionMat.m22 = (1F - 2F * plane[2] * plane[2]);
            reflectionMat.m23 = (-2F * plane[3] * plane[2]);

            reflectionMat.m30 = 0F;
            reflectionMat.m31 = 0F;
            reflectionMat.m32 = 0F;
            reflectionMat.m33 = 1F;
        }
    }
}