using System.Collections.Generic;
using System.Linq;

public static class SimpleLocalizer
{
    public static ISimpleLocalizerBase Init(string? locateCode = null)
    {
        locateCode ??= System.Globalization.CultureInfo.CurrentUICulture.Name;
        if (ISimpleLocalizerBase.GetMapping().TryGetValue(locateCode, out ISimpleLocalizerBase? localizer))
        {
            return localizer;
        }

        return ISimpleLocalizerBase.GetMapping().First().Value;
    }
}
