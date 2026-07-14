namespace SceneBuilder.Core.Model
{
    public readonly record struct Color(float R, float G, float B, float A)
    {
        public static readonly Color Clear = new(0, 0, 0, 0);
    }
}
