using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Networking;

namespace EmbervilleDiagnostic
{
    public static class FastImages
    {
        static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Texture2D> ToValidate = new Dictionary<string, Texture2D>();
        static object Field(object target, string key) => target.GetType().GetField(key, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        public static async UniTask Save(string path, RenderTexture rt)
        {
            long span = Probe.Begin("ImageHelper.SaveRenderTextureAsync");
            var data = new NativeArray<byte>((int)GraphicsFormatUtility.ComputeMipmapSize(rt.width, rt.height, rt.graphicsFormat), Allocator.Persistent);
            NativeArray<byte> encoded = default;
            int width = rt.width, height = rt.height;
            var format = rt.graphicsFormat;
            try
            {
                await AsyncGPUReadback.RequestIntoNativeArrayAsync(ref data, rt, 0, format);
                await UniTask.SwitchToTaskPool();
                encoded = ImageConversion.EncodeNativeArrayToPNG(data, format, (uint)width, (uint)height);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(encoded.AsReadOnlySpan());
                    stream.Flush(true);
                }

                await UniTask.SwitchToMainThread();
                var texture = new Texture2D(width, height, format, TextureCreationFlags.None);
                texture.LoadRawTextureData(data);
                texture.Apply(false, true);
                texture.filterMode = FilterMode.Point;
                if (Textures.TryGetValue(path, out var old))
                    UnityEngine.Object.Destroy(old);
                Textures[path] = texture;
                Probe.Mark("images.retained:" + Path.GetFileName(path));
            }
            finally
            {
                if (encoded.IsCreated)
                    encoded.Dispose();
                if (data.IsCreated)
                    data.Dispose();
                Probe.End(span, "ImageHelper.SaveRenderTextureAsync", "end");
            }
        }

        public static async UniTask Load(WorldMapContainer container, DungeonRender dungeon, string fileName)
        {
            long span = Probe.Begin("WorldMapContainer.LoadMapImages");
            int size = (int)Field(container, "imageSize"), step = size * 2;
            var prefab = (SpriteRenderer)Field(container, "rawImagePrefab");
            var instances = (List<GameObject>)Field(container, "instantiated");
            var bl = new Vector2Int(dungeon.TotalBottomLeft.x * 6 + size - 1, dungeon.TotalBottomLeft.y * 6 + size - 1);
            var ur = new Vector2Int(dungeon.TotalUpperRight.x * 6 + size + 1, dungeon.TotalUpperRight.y * 6 + size + 1);
            for (int i = bl.x; i <= ur.x; i += step)
                for (int j = bl.y; j <= ur.y; j += step)
                {
                    string path = Probe.DataPath() + $"/{fileName}({i},{j}).png";
                    Texture2D texture;
                    if (Textures.TryGetValue(path, out texture))
                    {
                        Textures.Remove(path);
                        if (BenchHooks.Has("-bench-auto"))
                            ToValidate[path] = texture;
                        Probe.Mark("images.reused:" + Path.GetFileName(path));
                    }
                    else
                    {
                        using (var request = UnityWebRequestTexture.GetTexture("file://" + path))
                        {
                            await request.SendWebRequest();
                            texture = DownloadHandlerTexture.GetContent(request);
                        }
                    }

                    texture.filterMode = FilterMode.Point;
                    var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), Vector2.zero, 1f);
                    var renderer = UnityEngine.Object.Instantiate(prefab, prefab.transform.parent);
                    renderer.gameObject.SetActive(true);
                    renderer.sprite = sprite;
                    renderer.transform.localPosition = new Vector3(i - size, j - size, 0);
                    renderer.transform.localScale = new Vector3(step / 6000f, step / 6000f, 0);
                    var owner = renderer.gameObject.AddComponent<MapTextureOwner>();
                    owner.Texture = texture;
                    owner.Sprite = sprite;
                    instances.Add(renderer.gameObject);
                    await BackgroundTaskManager.SplitTask();
                }

            prefab.gameObject.SetActive(false);
            Probe.Mark("images.pending:" + Textures.Count);
            Probe.End(span, "WorldMapContainer.LoadMapImages", "ok");
        }

        public static async UniTask Validate()
        {
            if (!BenchHooks.Has("-bench-auto") || ToValidate.Count == 0)
                return;
            var results = new List<object>();
            foreach (var pair in ToValidate)
            {
                var request = AsyncGPUReadback.Request(pair.Value, 0, TextureFormat.RGBA32);
                await request;
                if (request.hasError)
                    throw new Exception("Texture verification readback failed");
                var actual = request.GetData<Color32>();
                var expectedTexture = new Texture2D(2, 2);
                ImageConversion.LoadImage(expectedTexture, File.ReadAllBytes(pair.Key), false);
                var expected = expectedTexture.GetPixels32();
                long different = 0;
                for (int i = 0; i < expected.Length; i++)
                {
                    var a = actual[i];
                    var b = expected[i];
                    if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a)
                        different++;
                }

                results.Add(new { image = Path.GetFileName(pair.Key), pixels = expected.Length, different });
                UnityEngine.Object.Destroy(expectedTexture);
                if (different != 0)
                    throw new Exception("Cached texture differs from PNG: " + different + " pixels");
            }

            File.WriteAllText(Path.Combine(BenchHooks.Output, "texture-validation.json"), Newtonsoft.Json.JsonConvert.SerializeObject(results, Newtonsoft.Json.Formatting.Indented));
            ToValidate.Clear();
        }
    }

    public sealed class MapTextureOwner : MonoBehaviour
    {
        public Texture2D Texture;
        public Sprite Sprite;
        void OnDestroy()
        {
            if (Sprite)
                Destroy(Sprite);
            if (Texture)
                Destroy(Texture);
        }
    }
}
