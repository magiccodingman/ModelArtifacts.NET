using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ModelArtifacts.Native;

internal static unsafe class NativeExports
{
    [UnmanagedCallersOnly(EntryPoint = "ma_abi_version", CallConvs = [typeof(CallConvCdecl)])]
    public static uint AbiVersion() => NativeRuntime.AbiVersion;

    [UnmanagedCallersOnly(EntryPoint = "ma_get_last_error", CallConvs = [typeof(CallConvCdecl)])]
    public static int GetLastError(MaBuffer* output)
    {
        try { NativeRuntime.WriteBuffer(NativeRuntime.LastError, output); return (int)MaStatus.Ok; }
        catch { return (int)MaStatus.InternalError; }
    }

    [UnmanagedCallersOnly(EntryPoint = "ma_buffer_free", CallConvs = [typeof(CallConvCdecl)])]
    public static void BufferFree(MaBuffer* buffer)
    {
        if (buffer is null) return;
        if (buffer->Data is not null) NativeMemory.Free(buffer->Data);
        buffer->Data = null;
        buffer->Length = 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "ma_manager_create", CallConvs = [typeof(CallConvCdecl)])]
    public static int ManagerCreate(MaManagerOptions* options, nint* outputHandle)
    {
        NativeRuntime.ClearError();
        ModelArtifacts.ArtifactManager? manager = null;
        try
        {
            if (options is null || outputHandle is null) throw new ArgumentNullException("options and outputHandle are required.");
            *outputHandle = 0;
            if (options->AbiVersion != 0 && options->AbiVersion != NativeRuntime.AbiVersion) throw new ArgumentException($"Unsupported ABI version {options->AbiVersion}.");
            if (options->StructSize < (uint)sizeof(MaManagerOptions)) throw new ArgumentException("ma_manager_options.struct_size is smaller than the v1 structure.");
            var json = NativeRuntime.ReadUtf8(options->ConfigJson, options->ConfigJsonLength);
            manager = NativeRuntime.CreateManager(json);
            *outputHandle = NativeRuntime.AddManager(manager);
            manager = null;
            return (int)MaStatus.Ok;
        }
        catch (Exception ex)
        {
            manager?.Dispose();
            NativeRuntime.SetError(ex);
            return (int)(ex is InvalidHandleException ? MaStatus.InvalidHandle : NativeRuntime.MapException(ex));
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "ma_manager_destroy", CallConvs = [typeof(CallConvCdecl)])]
    public static int ManagerDestroy(nint handle)
    {
        NativeRuntime.ClearError();
        try
        {
            if (handle == 0) return (int)MaStatus.Ok;
            NativeRuntime.RemoveManager(handle).Dispose();
            return (int)MaStatus.Ok;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ma_manager_resolve_candidate", CallConvs = [typeof(CallConvCdecl)])]
    public static int ManagerResolveCandidate(nint managerHandle, int forceRemoteCheck, nint* outputCandidateHandle)
    {
        NativeRuntime.ClearError();
        try
        {
            if (outputCandidateHandle is null) throw new ArgumentNullException(nameof(outputCandidateHandle));
            *outputCandidateHandle = 0;
            var candidate = NativeRuntime.GetManager(managerHandle).ResolveCandidateAsync(forceRemoteCheck != 0).GetAwaiter().GetResult();
            *outputCandidateHandle = NativeRuntime.AddCandidate(candidate);
            return (int)MaStatus.Ok;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ma_candidate_metadata_json", CallConvs = [typeof(CallConvCdecl)])]
    public static int CandidateMetadataJson(nint candidateHandle, MaBuffer* output)
    {
        NativeRuntime.ClearError();
        try
        {
            NativeRuntime.WriteBuffer(NativeRuntime.CandidateMetadataJson(NativeRuntime.GetCandidate(candidateHandle)), output);
            return (int)MaStatus.Ok;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ma_candidate_path", CallConvs = [typeof(CallConvCdecl)])]
    public static int CandidatePath(nint candidateHandle, MaBuffer* output)
    {
        NativeRuntime.ClearError();
        try
        {
            NativeRuntime.WriteBuffer(NativeRuntime.GetCandidate(candidateHandle).Snapshot.DirectoryPath, output);
            return (int)MaStatus.Ok;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ma_candidate_promote", CallConvs = [typeof(CallConvCdecl)])]
    public static int CandidatePromote(nint managerHandle, nint candidateHandle, int cleanupObsoleteSnapshots)
    {
        NativeRuntime.ClearError();
        try
        {
            var manager = NativeRuntime.GetManager(managerHandle);
            var candidate = NativeRuntime.GetCandidate(candidateHandle);
            manager.PromoteAsync(candidate, cleanupObsoleteSnapshots != 0).GetAwaiter().GetResult();
            NativeRuntime.RemoveCandidate(candidateHandle);
            return (int)MaStatus.Ok;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ma_candidate_discard", CallConvs = [typeof(CallConvCdecl)])]
    public static int CandidateDiscard(nint managerHandle, nint candidateHandle)
    {
        NativeRuntime.ClearError();
        try
        {
            var manager = NativeRuntime.GetManager(managerHandle);
            var candidate = NativeRuntime.GetCandidate(candidateHandle);
            manager.DiscardAsync(candidate).GetAwaiter().GetResult();
            NativeRuntime.RemoveCandidate(candidateHandle);
            return (int)MaStatus.Ok;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    [UnmanagedCallersOnly(EntryPoint = "ma_manager_cleanup", CallConvs = [typeof(CallConvCdecl)])]
    public static int ManagerCleanup(nint managerHandle)
    {
        NativeRuntime.ClearError();
        try
        {
            NativeRuntime.GetManager(managerHandle).CleanupAsync().GetAwaiter().GetResult();
            return (int)MaStatus.Ok;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static int Fail(Exception ex)
    {
        NativeRuntime.SetError(ex);
        return (int)(ex is InvalidHandleException ? MaStatus.InvalidHandle : NativeRuntime.MapException(ex));
    }
}
