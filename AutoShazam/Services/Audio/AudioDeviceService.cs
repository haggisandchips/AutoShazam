using System.Runtime.InteropServices;
using AutoShazam.Models;
using NAudio.CoreAudioApi;

namespace AutoShazam.Services.Audio;

internal sealed class AudioDeviceService
{
    public List<AudioDeviceOption> GetCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceOption(d.ID, d.FriendlyName))
            .ToList();
    }

    public string? GetDefaultDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public MMDevice? GetDeviceById(string? id)
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            return string.IsNullOrEmpty(id)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : enumerator.GetDevice(id);
        }
        catch (COMException)
        {
            return null;
        }
    }
}
