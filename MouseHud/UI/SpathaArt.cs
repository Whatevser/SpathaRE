namespace MouseHud.UI;

/// <summary>A clickable button area on the artwork. Coordinates are in image pixels
/// (image drawn at Width x Height); Anchor is where the leader line meets the button,
/// Box is the callout's top-left on the canvas, Left = callout sits left of the mouse.</summary>
public sealed record MapRegion(int Slot, (double X, double Y)[] Points, (double X, double Y) Anchor,
    (double X, double Y) Box, bool Left);

public sealed record MapView(string Title, string Image, double Width, double Height, double OffsetX, MapRegion[] Regions);

/// <summary>
/// Geometry of the Spatha button map (top and left-side views). Slot numbers are the
/// original Spatha's; other models translate through <see cref="SlotMapLayout.SlotFor"/>.
/// Generated from spatha_slot_map.html.
/// </summary>
public static class SpathaArt
{
    public static readonly MapView Top = new(
        "Top", "spatha_top.png", 275, 400, 144,
        new MapRegion[]
        {
            new(0, new (double, double)[] { (128, 31), (80, 43), (79, 210), (129, 181) }, (103.6, 117.0), (8, 101.0), true),
            new(1, new (double, double)[] { (159, 25), (158, 26), (158, 170), (218, 187), (218, 19), (210, 18), (204, 20), (199, 20), (174, 25) }, (188.8, 100.3), (471, 70.2), false),
            new(2, new (double, double)[] { (136, 102), (136, 121), (155, 121), (155, 102) }, (145.5, 111.5), (471, 114.2), false),
            new(3, new (double, double)[] { (68, 101), (62, 108), (64, 155), (70, 160), (73, 160), (77, 149), (77, 103), (78, 102) }, (70.0, 129.2), (8, 145.0), true),
            new(4, new (double, double)[] { (78, 43), (65, 44), (59, 49), (62, 106), (67, 100), (78, 100) }, (69.2, 71.9), (8, 57.0), true),
            new(5, new (double, double)[] { (157, 153), (134, 147), (134, 183), (157, 170) }, (144.1, 163.5), (471, 202.2), false),
            new(12, new (double, double)[] { (138, 81), (136, 83), (136, 100), (155, 100), (155, 84), (153, 81) }, (145.5, 90.6), (471, 26.2), false),
            new(13, new (double, double)[] { (136, 120), (136, 138), (139, 140), (152, 140), (154, 139), (155, 137), (155, 120) }, (145.5, 129.9), (471, 158.2), false),
        });

    public static readonly MapView Side = new(
        "Left side", "spatha_side.png", 150, 400, 144,
        new MapRegion[]
        {
            new(6, new (double, double)[] { (71, 154), (72, 197), (79, 216), (84, 235), (86, 251), (88, 253), (119, 230), (120, 225), (114, 212) }, (91.9, 209.4), (346, 211.5), false),
            new(7, new (double, double)[] { (117, 230), (85, 254), (86, 273), (87, 274), (87, 300), (98, 305), (105, 290), (110, 276), (116, 249) }, (99.9, 267.6), (346, 255.5), false),
            new(8, new (double, double)[] { (69, 197), (61, 237), (83, 253), (79, 227), (74, 212), (74, 209), (70, 197) }, (71.7, 228.0), (8, 207.8), true),
            new(9, new (double, double)[] { (61, 241), (61, 247), (58, 262), (57, 280), (85, 297), (86, 296), (86, 275), (85, 274), (85, 261), (84, 258) }, (72.0, 269.9), (8, 295.8), true),
            new(10, new (double, double)[] { (67, 148), (65, 148), (55, 162), (46, 178), (46, 180), (34, 205), (34, 210), (37, 214), (60, 236), (64, 214), (69, 198), (69, 171), (68, 170), (68, 149) }, (54.9, 194.2), (8, 163.8), true),
            new(11, new (double, double)[] { (33, 214), (33, 262), (54, 278), (56, 254), (59, 240) }, (44.3, 247.3), (8, 251.8), true),
        });
}
