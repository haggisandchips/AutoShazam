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
            .Select(d => new AudioDeviceOption(d.ID, d.FriendlyName, AudioSourceKind.Microphone))
            .ToList();
    }

    public List<AudioDeviceOption> GetRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceOption(d.ID, d.FriendlyName, AudioSourceKind.Speaker))
            .ToList();
    }

    public MMDevice? GetDeviceById(string? id, AudioSourceKind kind)
    {
        var flow = kind == AudioSourceKind.Speaker ? DataFlow.Render : DataFlow.Capture;
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            return string.IsNullOrEmpty(id)
                ? enumerator.GetDefaultAudioEndpoint(flow, Role.Console)
                : enumerator.GetDevice(id);
        }
        catch (COMException)
        {
            return null;
        }
    }
}
