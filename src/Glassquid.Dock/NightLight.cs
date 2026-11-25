using Microsoft.Win32;

namespace Glassquid.Dock;

/// <summary>Reads and toggles the Night light state stored by Windows CloudStore.</summary>
internal static class NightLight
{
    private const string StoreRoot =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount";
    private const string StateName = "bluelightreductionstate";
    private const string DataValueName = "Data";

    private static readonly object ToggleSync = new();
    private static ulong _highestTimestamp;
    private static bool _startupMarginApplied;

    private readonly record struct StateRecord(
        string RegistryPath, string CloudRecordKeyPath, bool IsPerDevice, byte[] Data);

    public static bool? IsEnabled()
    {
        var records = ReadStateRecords();
        var primary = records.FirstOrDefault(record => !record.IsPerDevice);
        if (primary.Data is not null && TryReadEnabled(primary, out var enabled)) return enabled;

        foreach (var record in records)
        {
            if (TryReadEnabled(record, out enabled)) return enabled;
        }

        if (NightLightDisplayEffect.IsActive) return true;

        return null;
    }

    public static bool? Toggle()
    {
        lock (ToggleSync)
        {
            var records = ReadStateRecords();
            var primary = records.FirstOrDefault(record => !record.IsPerDevice);
            bool current = false;
            bool found = false;

            if (primary.Data is not null && TryReadEnabled(primary, out var pState))
            {
                current = pState;
                found = true;
            }
            else
            {
                foreach (var r in records)
                {
                    if (TryReadEnabled(r, out var rState))
                    {
                        current = rState;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                current = NightLightDisplayEffect.IsActive;
                found = true;
            }

            var target = !current;
            var timestamp = NextTimestamp(records);
            var updates = new List<(StateRecord Record, byte[] Updated)>();
            foreach (var record in records)
            {
                if (TryCreateUpdatedState(record, target, timestamp, out var updated))
                    updates.Add((record, updated));
            }

            var written = new List<StateRecord>();
            try
            {
                foreach (var (record, updated) in updates)
                {
                    using var key = Registry.CurrentUser.OpenSubKey(record.RegistryPath, writable: true);
                    if (key != null)
                    {
                        key.SetValue(DataValueName, updated, RegistryValueKind.Binary);
                        key.Flush();
                        written.Add(record);
                    }

                    try
                    {
                        using var cloudKey = Registry.CurrentUser.OpenSubKey(record.CloudRecordKeyPath, writable: true);
                        if (cloudKey != null)
                        {
                            var cloudEnvelope = NightLightCloudStore.WriteCloudRecordEnvelope(timestamp);
                            cloudKey.SetValue(DataValueName, cloudEnvelope, RegistryValueKind.Binary);
                            cloudKey.Flush();
                        }
                    }
                    catch { /* best effort */ }
                }
            }
            catch
            {
                // Keep the default and per-device records aligned if a mirror cannot be written.
                foreach (var record in written)
                {
                    try
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(record.RegistryPath, writable: true);
                        key?.SetValue(DataValueName, record.Data, RegistryValueKind.Binary);
                        key?.Flush();
                    }
                    catch
                    {
                        // Preserve the original write error; rollback is best effort.
                    }
                }
                throw;
            }

            // Update physical screen warmth via hardware-accelerated DWM Magnification API
            if (target)
            {
                int strength = 50;
                try
                {
                    strength = NightLightCloudStore.ReadStrength();
                }
                catch { }
                NightLightDisplayEffect.Apply(strength);
            }
            else
            {
                NightLightDisplayEffect.Restore();
            }

            return target;
        }
    }

    private static List<StateRecord> ReadStateRecords()
    {
        var records = new List<StateRecord>();
        using var root = Registry.CurrentUser.OpenSubKey(StoreRoot, writable: false);
        if (root is null) return records;

        foreach (var branchName in root.GetSubKeyNames())
        {
            using var branch = root.OpenSubKey(branchName, writable: false);
            if (branch is null) continue;

            foreach (var containerName in branch.GetSubKeyNames().Where(name =>
                         name.Contains(StateName, StringComparison.OrdinalIgnoreCase)))
            {
                var containerPath = $@"{StoreRoot}\{branchName}\{containerName}";
                ReadStateRecordsBelow(containerPath, branchName, containerName, records);
            }
        }

        return records;
    }

    private static void ReadStateRecordsBelow(
        string registryPath, string branchName, string containerName,
        List<StateRecord> records)
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: false);
        if (key is null) return;

        if (key.GetValue(DataValueName) is byte[] data && data.Length >= 25)
        {
            var isPerDevice = containerName.Contains("stateperdevice", StringComparison.OrdinalIgnoreCase);
            var branchPrefix = $@"{StoreRoot}\{branchName}\";
            var cloudPath = registryPath.StartsWith(branchPrefix, StringComparison.OrdinalIgnoreCase)
                ? $@"{StoreRoot}\Cloud\{registryPath[branchPrefix.Length..]}"
                : registryPath;
            var record = new StateRecord(registryPath, cloudPath, isPerDevice, data);
            if (TryReadEnabled(record, out _)) records.Add(record);
        }

        foreach (var childName in key.GetSubKeyNames())
            ReadStateRecordsBelow($@"{registryPath}\{childName}", branchName, containerName, records);
    }

    private static bool TryReadEnabled(StateRecord record, out bool enabled)
    {
        enabled = false;
        try
        {
            var envelope = NightLightCloudStore.ReadStateEnvelope(record.Data);
            var hasBaseStop = envelope.Payload.Length >= 2
                && envelope.Payload[^2] == 1 && envelope.Payload[^1] == 0;
            var fields = NightLightCloudStore.ReadMarshaledStruct(
                envelope.Payload, allowTerminalBaseStop: hasBaseStop);
            enabled = fields.Any(f => f.Id == 0);
            return true;
        }
        catch
        {
            // Legacy offset-based fallback for older Windows formats.
            return TryReadEnabledByOffset(record, out enabled);
        }
    }

    private static bool TryReadEnabledByOffset(StateRecord record, out bool enabled)
    {
        enabled = false;
        const int stateOffset = 18;
        if (record.Data.Length <= stateOffset) return false;

        var onMarker = record.IsPerDevice ? (byte)0x16 : (byte)0x15;
        var offMarker = record.IsPerDevice ? (byte)0x14 : (byte)0x13;
        var marker = record.Data[stateOffset];
        if (marker == onMarker)
        {
            enabled = true;
            return true;
        }

        return marker == offMarker;
    }

    private static bool TryCreateUpdatedState(
        StateRecord record, bool enabled, ulong timestamp, out byte[] updated)
    {
        updated = [];
        if (!TryReadEnabled(record, out _)) return false;

        try
        {
            var envelope = NightLightCloudStore.ReadStateEnvelope(record.Data);
            var hasBaseStop = envelope.Payload.Length >= 2
                && envelope.Payload[^2] == 1 && envelope.Payload[^1] == 0;
            var fields = NightLightCloudStore.ReadMarshaledStruct(
                envelope.Payload, allowTerminalBaseStop: hasBaseStop);

            var activationIdx = fields.FindIndex(f => f.Id == 0);
            var isOn = activationIdx >= 0;

            if (isOn == enabled)
            {
                // Already in desired state; bump timestamp so the record is refreshed.
                updated = NightLightCloudStore.WriteStateEnvelope(timestamp, envelope.Payload);
                return true;
            }

            if (enabled)
            {
                fields.Insert(0, new NightLightCloudStore.Field(
                    0, NightLightCloudStore.Int32Type,
                    NightLightCloudStore.EncodeVarUInt64(0)));
            }
            else
            {
                fields.RemoveAt(activationIdx);
            }

            var newPayload = NightLightCloudStore.WriteMarshaledStruct(
                fields.OrderBy(f => f.Id), hasBaseStop);
            updated = NightLightCloudStore.WriteStateEnvelope(timestamp, newPayload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ulong NextTimestamp(List<StateRecord> records)
    {
        var baseline = _highestTimestamp;
        foreach (var record in records)
        {
            try
            {
                var envelope = NightLightCloudStore.ReadStateEnvelope(record.Data);
                baseline = Math.Max(baseline, envelope.Timestamp);
            }
            catch { /* best effort */ }

            try
            {
                baseline = Math.Max(baseline,
                    NightLightCloudStore.ReadCloudRecordTimestamp(record.CloudRecordKeyPath));
            }
            catch { /* best effort */ }
        }

        var step = _startupMarginApplied ? 2UL : 699UL;
        var timestamp = Math.Max(
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            baseline > ulong.MaxValue - step ? ulong.MaxValue : baseline + step);
        _highestTimestamp = timestamp;
        _startupMarginApplied = true;
        return timestamp;
    }
}
