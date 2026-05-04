namespace FileRecoveryParser.Models;

public enum ImageSubcategory
{
    None        = 0,   // non-image files — never shown in filters
    Icon        = 1,
    Screenshot  = 2,
    Wallpaper   = 3,
    GameAsset   = 4,
    PersonPhoto = 5,
    Other       = 6,
}
