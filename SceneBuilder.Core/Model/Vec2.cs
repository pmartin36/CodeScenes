namespace SceneBuilder.Core.Model
{
    public readonly record struct Vec2(float X, float Y)
    {
        public static readonly Vec2 Zero = new(0, 0);
    }
}
