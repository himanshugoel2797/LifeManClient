using Android.Content;

namespace Lifeman.Client.Android.Services;

/// Cross-component handoff for the MediaProjection consent token.
///
/// MediaProjection consent is a per-process, per-user-action grant: the
/// system shows a dialog from MediaProjectionManager.CreateScreenCaptureIntent,
/// the user approves, and OnActivityResult hands back an Intent that the
/// process can later feed to MediaProjectionManager.GetMediaProjection to
/// open the projection. The Intent is the consent token — without it the
/// collector cannot capture frames, and Android won't let the FGS itself
/// raise the dialog.
///
/// We stash the consent here (mirroring the static-singleton pattern used
/// by LifemanService.CurrentOutbox / Windows RuntimeState) so the
/// PhoneScreenCaptureCollector running inside the foreground service can
/// pick it up after the user grants in MainActivity. Cleared by either
/// the user revoking grant (capture call throws → collector self-disables
/// and nulls this) or process death.
public static class MediaProjectionState
{
    private static Intent? s_consentData;
    private static int s_consentResultCode;

    /// The Intent returned from MediaProjection consent. Null until the
    /// user approves the system dialog at least once this process.
    public static Intent? ConsentData
    {
        get => Volatile.Read(ref s_consentData);
        set => Volatile.Write(ref s_consentData, value);
    }

    /// Activity result code paired with ConsentData. Required by
    /// MediaProjectionManager.GetMediaProjection alongside the Intent.
    public static int ConsentResultCode
    {
        get => Volatile.Read(ref s_consentResultCode);
        set => Volatile.Write(ref s_consentResultCode, value);
    }

    /// Drop the stashed consent — call after a capture failure that
    /// indicates the grant has been revoked, so the collector observes
    /// the missing-grant state on its next cycle.
    public static void Clear()
    {
        Volatile.Write(ref s_consentData, null);
        Volatile.Write(ref s_consentResultCode, 0);
    }
}
