using System.Collections.Generic;
using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// The painter's own undo steps: for each fill or stroke, the rectangle it changed with every channel's bytes before
    /// and after, and the channel it was painted in. Unity's Undo only records a step number on a small object, so Ctrl+Z
    /// never copies the whole mask. Positions count steps; the history starts at the number Unity's Undo holds now, so
    /// steps stay in line with it.
    /// </summary>
    public sealed class MaskHistory
    {
        sealed class Step
        {
            public RectInt Rect;
            public MaskChannel Channel;
            public byte[] Before;
            public byte[] After;
            public long Bytes => 2L * Before.Length;
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
        public bool Commit(RectInt area, float[] before, MaskCanvas canvas)
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
            if (maxX < 0) return false;

            for (int k = steps.Count - 1; k >= Position - first; k--)
            {
                bytes -= steps[k].Bytes;
                steps.RemoveAt(k);
            }
            var rect = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
            var step = new Step { Rect = rect, Channel = canvas.Channel, Before = canvas.PixelsIn(rect) };
            canvas.Commit(rect);
            step.After = canvas.PixelsIn(rect);
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
                canvas.RestorePixels(step.Rect, step.Before);
                Position--;
            }
            while (Position < position)
            {
                var step = steps[Position - first];
                Show(step, canvas);
                canvas.RestorePixels(step.Rect, step.After);
                Position++;
            }
        }

        static void Show(Step step, MaskCanvas canvas)
        {
            if (canvas.Channel != step.Channel) canvas.SelectChannel(step.Channel);
        }
    }
}
