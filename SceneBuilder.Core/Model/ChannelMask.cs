namespace SceneBuilder.Core.Model
{
    [System.Flags]
    public enum ChannelMask
    {
        None      = 0,
        PositionX = 1 << 0,
        PositionY = 1 << 1,
        PositionZ = 1 << 2,
        ScaleX    = 1 << 3,
        ScaleY    = 1 << 4,
        ScaleZ    = 1 << 5,
        Scale = ScaleX | ScaleY | ScaleZ,
    }
}
