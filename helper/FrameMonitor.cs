using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EmbervilleDiagnostic
{
    public class AssetFrameMonitor : MonoBehaviour
    {
        public string FileStem = "background-frames";
        public static AssetFrameMonitor Current;
        public static void Stop()
        {
            if (Current)
                Current.Finish();
        }

        readonly List<double> frames = new List<double>();
        readonly List<object> spikes = new List<object>();
        readonly List<object> collections = new List<object>();
        float began;
        bool written;
        long last, started;
        int previousGc;
        void Awake()
        {
            Current = this;
            began = Time.realtimeSinceStartup;
            last = started = System.Diagnostics.Stopwatch.GetTimestamp();
            previousGc = GC.CollectionCount(0);
            Application.quitting += Finish;
        }

        void LateUpdate()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double gap = (now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            last = now;
            int gc = GC.CollectionCount(0), delta = gc - previousGc;
            previousGc = gc;
            double elapsed = (now - started) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (Time.realtimeSinceStartup - began < 0.2f)
                return;
            frames.Add(gap);
            if (delta != 0)
                collections.Add(new { elapsed_s = elapsed, gcCount = gc, delta, frame_ms = gap });
            if (gap > 33.333)
                spikes.Add(new { elapsed_s = elapsed, frame_ms = gap, gcCount = gc, gcDelta = delta, focused = Application.isFocused });
            if (Time.realtimeSinceStartup - began > 120)
                Finish();
        }

        void Finish()
        {
            if (written)
                return;
            written = true;
            frames.Sort();
            if (frames.Count > 0)
            {
                double P(double q) => frames[Math.Min(frames.Count - 1, (int)(frames.Count * q))];
                File.WriteAllText(Path.Combine(BenchHooks.Output, FileStem + ".json"), Newtonsoft.Json.JsonConvert.SerializeObject(new { count = frames.Count, p50_ms = P(.5), p95_ms = P(.95), p99_ms = P(.99), max_ms = frames.Last(), over33_ms = frames.Count(x => x > 33.333), over50_ms = frames.Count(x => x > 50), spikes, collections }, Newtonsoft.Json.Formatting.Indented));
            }

            enabled = false;
            Application.quitting -= Finish;
        }

        void OnDestroy()
        {
            Finish();
        }
    }
}
