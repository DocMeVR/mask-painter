using UnityEngine;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>What the Scene view draws over the model. The numbers are the ones 1.2.0 stored.</summary>
    public enum MaskShow { Overlay, MaskOnly, Nothing, EveryChannel }

    /// <summary>The colours the Scene view and the brush ring use for each channel.</summary>
    public static class MaskLook
    {
        static readonly Color All = new Color(0.25f, 0.79f, 0.66f);
        static readonly Color Red = new Color(0.95f, 0.27f, 0.27f);
        static readonly Color Green = new Color(0.30f, 0.88f, 0.36f);
        static readonly Color Blue = new Color(0.30f, 0.55f, 1.00f);
        static readonly Color Alpha = new Color(0.78f, 0.45f, 1.00f);

        public static Color Tint(MaskChannel channel)
        {
            switch (channel)
            {
                case MaskChannel.Red: return Red;
                case MaskChannel.Green: return Green;
                case MaskChannel.Blue: return Blue;
                case MaskChannel.Alpha: return Alpha;
                default: return All;
            }
        }

        /// <summary>Which of red, green, blue and alpha (0 to 3) holds a channel's values; All shows red.</summary>
        public static int Offset(MaskChannel channel) => channel == MaskChannel.All ? 0 : (int)channel - 1;
    }
}
