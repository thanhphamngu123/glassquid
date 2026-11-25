using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Glassquid.Dock;
using Xunit;
using Xunit.Abstractions;

namespace Glassquid.Dock.Tests;

public class AudioDeviceManagerProbeTests
{
    private readonly ITestOutputHelper _output;

    public AudioDeviceManagerProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TestAudioEndpointVolumeController_And_GetPlaybackDevices()
    {
        StaTest.Run(() =>
        {
            using var controller = new AudioEndpointVolumeController();
            var masterVol = controller.GetMasterVolumePercent();
            _output.WriteLine($"AudioEndpointVolumeController initialized, Master Volume = {masterVol}%");

            var devices = AudioDeviceManager.GetPlaybackDevices(out var defaultId);
            _output.WriteLine($"GetPlaybackDevices returned {devices.Count} devices while controller is active! Default ID: {defaultId}");
            foreach (var dev in devices)
            {
                _output.WriteLine($"  Device: '{dev.Name}', IsDef={dev.IsDefault}, Glyph='{dev.IconGlyph}'");
            }

            Assert.NotEmpty(devices);
        });
    }

    [Fact]
    public void TestSoundOutputFlyoutWindow_PopulatesDevices()
    {
        StaTest.Run(() =>
        {
            var dock = new SystemTrayBarWindow();
            dock.Show();
            try
            {
                var flyout = new SoundOutputFlyoutWindow(dock);
                flyout.PrepareForShow();

                var itemsSource = flyout.OutputDevicesList.ItemsSource as IList;
                _output.WriteLine($"After PrepareForShow: ItemsSource count = {itemsSource?.Count}");
                _output.WriteLine($"Window Width = {flyout.Width}, Height = {flyout.Height}");

                Assert.NotNull(itemsSource);
                Assert.NotEmpty(itemsSource);

                flyout.Show();
                _output.WriteLine($"After Show: OutputDevicesList.Items.Count = {flyout.OutputDevicesList.Items.Count}");
                flyout.Close();
            }
            finally
            {
                dock.Close();
            }
        });
    }

    [Fact]
    public void TestSpatialAudio()
    {
        StaTest.Run(() =>
        {
            var devices = AudioDeviceManager.GetPlaybackDevices(out var defaultId);
            _output.WriteLine($"Default MMDevice ID: {defaultId}");
            try
            {
                var directCfg = Windows.Media.Audio.SpatialAudioDeviceConfiguration.GetForDeviceId(defaultId);
                _output.WriteLine($"Direct MMDevice ID config: IsSupported={directCfg.IsSpatialAudioSupported}, Active='{directCfg.ActiveSpatialAudioFormat}'");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Direct MMDevice ID failed: {ex.Message}");
            }

            var selector = Windows.Media.Devices.MediaDevice.GetAudioRenderSelector();
            var winRtDevices = Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector).GetAwaiter().GetResult();
            var matched = winRtDevices.FirstOrDefault(d => d.Id.Contains(defaultId, StringComparison.OrdinalIgnoreCase));
            _output.WriteLine($"Matched WinRT Device: {matched?.Name} ({matched?.Id})");
            if (matched != null)
            {
                var cfg = Windows.Media.Audio.SpatialAudioDeviceConfiguration.GetForDeviceId(matched.Id);
                _output.WriteLine($"WinRT config: IsSupported={cfg.IsSpatialAudioSupported}, Active='{cfg.ActiveSpatialAudioFormat}', Def='{cfg.DefaultSpatialAudioFormat}'");

                // Test setting Off
                var setOff = cfg.SetDefaultSpatialAudioFormatAsync(string.Empty).GetAwaiter().GetResult();
                _output.WriteLine($"SetOff Result Status = {setOff.Status}");
            }
        });
    }

    [Fact]
    public void TestSetDefaultDevice()
    {
        StaTest.Run(() =>
        {
            var devices = AudioDeviceManager.GetPlaybackDevices(out var defaultId);
            _output.WriteLine($"Current default ID: {defaultId}");
            foreach (var d in devices)
            {
                var policy = (AudioDeviceManager.IPolicyConfig)new AudioDeviceManager.CPolicyConfigClient();
                var hr1 = policy.SetDefaultEndpoint(d.Id, AudioDeviceManager.ERole.eMultimedia);
                var hr2 = policy.SetDefaultEndpoint(d.Id, AudioDeviceManager.ERole.eConsole);
                var hr3 = policy.SetDefaultEndpoint(d.Id, AudioDeviceManager.ERole.eCommunications);
                _output.WriteLine($"  hr Multimedia = 0x{hr1:X8}, hr Console = 0x{hr2:X8}, hr Comm = 0x{hr3:X8}");

                var updatedDevices = AudioDeviceManager.GetPlaybackDevices(out var newDefaultId);
                _output.WriteLine($"  After switch, default ID is: {newDefaultId}");
                _output.WriteLine($"  Did it match requested ID? {string.Equals(d.Id, newDefaultId, StringComparison.OrdinalIgnoreCase)}");

                var enumerator = AudioDeviceManager.CreateDeviceEnumerator();
                if (enumerator != null)
                {
                    enumerator.GetDefaultAudioEndpoint(0, (int)AudioDeviceManager.ERole.eCommunications, out var commDev);
                    if (commDev != null)
                    {
                        commDev.GetId(out var commId);
                        _output.WriteLine($"  Current Communications Default ID: {commId}");
                        _output.WriteLine($"  Did Comm match requested? {string.Equals(d.Id, commId, StringComparison.OrdinalIgnoreCase)}");
                    }
                }
            }

            // Restore original default
            if (!string.IsNullOrEmpty(defaultId))
            {
                AudioDeviceManager.SetDefaultPlaybackDevice(defaultId);
            }
        });
    }
}
