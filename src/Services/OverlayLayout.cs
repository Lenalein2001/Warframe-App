using System.Drawing;

namespace WarframeCompanion.Services;

public static class OverlayLayout
{
    public static Rectangle[] Boxes(OverlayRequest request)
    {
        var u = request.Unit; var midX = request.X + request.Width / 2.0; var midY = request.Y + request.Height / 2.0;
        if (request.Kind == "riven") return [new((int)(midX + 162 * u), (int)(midY - 95 * u), (int)(330 * u), (int)((154 + 44 * Math.Max(1, J.A(request.Result, "stats").Count())) * u))];
        var cards = (int)J.N(request.Result, "cards"); var count = cards == 0 ? 1 : cards; var cardWidth = cards == 0 ? 484.0 : 242.0;
        return Enumerable.Range(0, count).Select(k =>
        {
            var left = (k - count / 2.0) * cardWidth + 4; var width = cardWidth - 8; var at = 52.0; var h = 86 * u;
            if (request.Above)
            {
                at = 326; var right = left + width;
                if (count % 2 == 1 && k == count / 2) at = 423;
                else if (right > -70 && left < 70) { if (left < 0) width = -70 - left; else { width = right - 70; left = 70; } }
            }
            return new Rectangle((int)Math.Round(midX + left * u), (int)Math.Round(midY - at * u - (request.Above ? h : 0)), Math.Max(1, (int)Math.Round(width * u)), Math.Max(1, (int)Math.Round(h)));
        }).ToArray();
    }
}
