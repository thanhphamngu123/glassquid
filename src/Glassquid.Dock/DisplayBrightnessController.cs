using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Glassquid.Dock;

/// <summary>Uses Windows' WMI monitor brightness provider for displays that expose a brightness control.</summary>
internal static class DisplayBrightnessController
{
    public static int? TryGetCurrentPercent()
    {
        object? locator = null;
        object? service = null;
        object? monitors = null;
        try
        {
            locator = CreateLocator();
            dynamic dynamicLocator = locator;
            service = dynamicLocator.ConnectServer(".", @"root\wmi");
            dynamic dynamicService = service;
            monitors = dynamicService.ExecQuery(
                "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");

            foreach (var monitor in (System.Collections.IEnumerable)monitors)
            {
                try
                {
                    return Math.Clamp(Convert.ToInt32(GetWmiProperty(monitor, "CurrentBrightness"), CultureInfo.InvariantCulture), 0, 100);
                }
                finally
                {
                    ReleaseComObject(monitor);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Brightness] Could not read monitor brightness: {ex.Message}");
        }
        finally
        {
            ReleaseComObject(monitors);
            ReleaseComObject(service);
            ReleaseComObject(locator);
        }

        return null;
    }

    public static bool TrySetPercent(int percent)
    {
        object? locator = null;
        object? service = null;
        object? methods = null;
        var applied = false;
        try
        {
            locator = CreateLocator();
            dynamic dynamicLocator = locator;
            service = dynamicLocator.ConnectServer(".", @"root\wmi");
            dynamic dynamicService = service;
            methods = dynamicService.ExecQuery(
                "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");

            foreach (var monitorMethods in (System.Collections.IEnumerable)methods)
            {
                object? methodDescription = null;
                object? inputParameters = null;
                object? outputParameters = null;
                try
                {
                    dynamic dynamicMethods = monitorMethods;
                    methodDescription = dynamicMethods.Methods_["WmiSetBrightness"];
                    dynamic dynamicDescription = methodDescription;
                    inputParameters = dynamicDescription.InParameters.SpawnInstance_();
                    // Keep the supported one-second transition. The slider now sends only
                    // the final thumb value, so a new request cannot interrupt this one.
                    SetWmiProperty(inputParameters, "Timeout", (uint)1);
                    SetWmiProperty(inputParameters, "Brightness", (byte)Math.Clamp(percent, 0, 100));
                    outputParameters = dynamicMethods.ExecMethod_("WmiSetBrightness", inputParameters);
                    var returnValue = Convert.ToUInt32(GetWmiProperty(outputParameters, "ReturnValue"), CultureInfo.InvariantCulture);
                    applied |= returnValue == 0;
                    if (returnValue != 0)
                        Debug.WriteLine($"[Glassquid Brightness] WmiSetBrightness returned {returnValue}.");
                }
                finally
                {
                    ReleaseComObject(outputParameters);
                    ReleaseComObject(inputParameters);
                    ReleaseComObject(methodDescription);
                    ReleaseComObject(monitorMethods);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Glassquid Brightness] Could not set monitor brightness: {ex.Message}");
        }
        finally
        {
            ReleaseComObject(methods);
            ReleaseComObject(service);
            ReleaseComObject(locator);
        }

        return applied;
    }

    private static object CreateLocator()
    {
        var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator")
            ?? throw new PlatformNotSupportedException("Windows WMI is unavailable.");
        return Activator.CreateInstance(locatorType)
            ?? throw new InvalidOperationException("Windows WMI could not create a locator.");
    }

    private static object? GetWmiProperty(object instance, string name)
    {
        object? properties = null;
        object? property = null;
        try
        {
            dynamic dynamicInstance = instance;
            properties = dynamicInstance.Properties_;
            dynamic dynamicProperties = properties;
            property = dynamicProperties.Item(name);
            dynamic dynamicProperty = property;
            return dynamicProperty.Value;
        }
        finally
        {
            ReleaseComObject(property);
            ReleaseComObject(properties);
        }
    }

    private static void SetWmiProperty(object instance, string name, object value)
    {
        object? properties = null;
        object? property = null;
        try
        {
            dynamic dynamicInstance = instance;
            properties = dynamicInstance.Properties_;
            dynamic dynamicProperties = properties;
            property = dynamicProperties.Item(name);
            dynamic dynamicProperty = property;
            dynamicProperty.Value = value;
        }
        finally
        {
            ReleaseComObject(property);
            ReleaseComObject(properties);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); }
            catch (InvalidComObjectException) { }
        }
    }
}
