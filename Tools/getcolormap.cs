using dc;
using dc.hxd;
using ModCore.Utilities;

namespace DeadCellsMultiplayerMod.Tools.Sprtool
{
    public static class Getpng
    {
        public static dc.h3d.mat.Texture? getColorMap(dc.String model, dc.String colorMap)
        {
            dc.String @string = "atlas/".AsHaxeString();
            dc._String _String =dc.String.Class;
            @string = _String.__add__(_String.__add__(_String.__add__(_String.__add__(@string, model), "_".AsHaxeString()), colorMap), "_s.png".AsHaxeString());
            if (!Res.Class.get_loader().exists(@string))
            {
                return null;
            }
            dc.h3d.mat.Texture texture = Res.Class.load(@string).toTexture();
            dc.h3d.mat.Filter filter = new dc.h3d.mat.Filter.Nearest();
            filter = texture.set_filter(filter);
            return texture;
        }
    }
}