using System.Collections.Generic;
using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// The painter's own undo steps: for each fill or stroke, the rectangles it changed (a stroke keeps one per tile it
    /// reached) with every channel's bytes before and after, and the channel it was painted in. Unity's Undo only records a step number on a small object, so Ctrl+Z
    /// never copies the whole mask. Positions count steps; the history starts at the number Unity's Undo holds now, so
    /// steps stay in line with it.
    /// </summary>
    public sealed class MaskHistory
    {
        sealed class Step
        {
            public RectInt[] Rects;
            public MaskChannel Channel;
            public byte[][] Before;
            public byte[][] After;
            public long Bytes;
        }

        /// <summary>The memory the steps may use; the oldest go first past it.</summary>
        public const long Budget = 256L << 20;

        readonly List<Step> steps = new List<Step>();
        int first;
        long bytes;

        public MaskHistory(int position)
        {
            first = position;
            Position = position;
        }

        public int Position { get; private set; }

        /// <summary>Keeps what changed from before (the whole canvas) to the canvas now as a new step. False when nothing changed.</summary>
        public bool Commit(float[] before, MaskCanvas canvas) => Commit(new RectInt(0, 0, canvas.Width, canvas.Height), before, canvas);

        /// <summary>
        /// The same for a change that stayed inside area, with before holding only that area's values. The change is
        /// written into the canvas's channels here, so the step holds them as they were and as they are.
        /// </summary>
        public bool Commit(RectInt area, float[] before, MaskCanvas canvas) => Commit(new[] { (area, before) }, canvas);

        /// <summary>The same for a change in several areas that do not overlap, each with its own values before, as one step.</summary>
        public bool Commit(IReadOnlyList<(RectInt area, float[] before)> parts, MaskCanvas canvas)
        {
            var rects = new List<RectInt>();
            foreach (var (area, before) in parts)
                if (Changed(area, before, canvas, out var rect)) rects.Add(rect);
            if (rects.Count == 0) return false;

            for (int k = steps.Count - 1; k >= Position - first; k--)
            {
                bytes -= steps[k].Bytes;
                steps.RemoveAt(k);
            }
            var step = new Step { Rects = rects.ToArray(), Channel = canvas.Channel, Before = new byte[rects.Count][], After = new byte[rects.Count][] };
            for (int k = 0; k < rects.Count; k++)
            {
                step.Before[k] = canvas.PixelsIn(rects[k]);
                canvas.Commit(rects[k]);
                step.After[k] = canvas.PixelsIn(rects[k]);
                step.Bytes += 2L * step.Before[k].Length;
            }
            steps.Add(step);
            bytes += step.Bytes;
            Position++;
            while (bytes > Budget && steps.Count > 1)
            {
                bytes -= steps[0].Bytes;
                steps.RemoveAt(0);
                first++;
            }
            return true;
        }

        /// <summary>The smallest rectangle inside area where the canvas differs from before (area's values, row by row).</summary>
        static bool Changed(RectInt area, float[] before, MaskCanvas canvas, out RectInt rect)
        {
            int w = canvas.Width;
            var now = canvas.Values;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = area.yMin; y < area.yMax; y++)
            {
                int row = y * w, local = (y - area.yMin) * area.width - area.xMin;
                for (int x = area.xMin; x < area.xMax; x++)
                {
                    if (before[local + x] == now[row + x]) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    maxY = y;
                }
            }
            rect = maxX < 0 ? default : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return maxX >= 0;
        }

        /// <summary>
        /// Undoes or redoes steps until the history is at position, as far as the kept steps reach. The canvas shows the
        /// channel of the last step it walked.
        /// </summary>
        public void MoveTo(int position, MaskCanvas canvas)
        {
            position = Mathf.Clamp(position, first, first + steps.Count);
            while (Position > position)
            {
                var step = steps[Position - first - 1];
                Show(step, canvas);
                for (int k = 0; k < step.Rects.Length; k++) canvas.RestorePixels(step.Rects[k], step.Before[k]);
                Position--;
            }
            while (Position < position)
            {
                var step = steps[Position - first];
                Show(step, canvas);
                for (int k = 0; k < step.Rects.Length; k++) canvas.RestorePixels(step.Rects[k], step.After[k]);
                Position++;
            }
        }

        static void Show(Step step, MaskCanvas canvas)
        {
            if (canvas.Channel != step.Channel) canvas.SelectChannel(step.Channel);
        }
    }
}
