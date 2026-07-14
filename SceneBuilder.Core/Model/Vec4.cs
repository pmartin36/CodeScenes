namespace SceneBuilder.Core.Model
{
    public readonly record struct Vec4(float X, float Y, float Z, float W)
    {
        public static readonly Vec4 Zero = new(0, 0, 0, 0);
    }
}
