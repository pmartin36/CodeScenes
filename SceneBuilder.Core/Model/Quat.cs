namespace SceneBuilder.Core.Model
{
    public readonly record struct Quat(float X, float Y, float Z, float W)
    {
        public static readonly Quat Identity = new(0, 0, 0, 1);
    }
}
