using System;
using System.Runtime.InteropServices;

namespace SftpExplorerWinUI.Services;

internal sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const string TargetPrefix = "SFTP Explorer/";

    public void Write(string connectionId, string username, string password)
    {
        IntPtr passwordBuffer = IntPtr.Zero;
        try
        {
            passwordBuffer = Marshal.StringToCoTaskMemUni(password);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = GetTargetName(connectionId),
                CredentialBlobSize = checked((uint)(password.Length * sizeof(char))),
                CredentialBlob = passwordBuffer,
                Persist = CredentialPersistLocalMachine,
                UserName = username
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new CredentialStoreException(
                    connectionId,
                    "write",
                    Marshal.GetLastWin32Error());
            }
        }
        catch (CredentialStoreException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CredentialStoreException(connectionId, "write", innerException: ex);
        }
        finally
        {
            if (passwordBuffer != IntPtr.Zero)
            {
                ClearMemory(passwordBuffer, checked(password.Length * sizeof(char)));
                Marshal.FreeCoTaskMem(passwordBuffer);
            }
        }
    }

    public StoredCredential? Read(string connectionId)
    {
        IntPtr credentialPointer = IntPtr.Zero;
        try
        {
            if (!CredRead(GetTargetName(connectionId), CredentialTypeGeneric, 0, out credentialPointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                    return null;

                throw new CredentialStoreException(connectionId, "read", error);
            }

            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero && credential.CredentialBlobSize != 0)
                throw new CredentialStoreException(connectionId, "read an invalid payload for");

            if (credential.CredentialBlobSize % sizeof(char) != 0)
                throw new CredentialStoreException(connectionId, "read an invalid payload for");

            var password = credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(
                    credential.CredentialBlob,
                    checked((int)credential.CredentialBlobSize / sizeof(char))) ?? string.Empty;

            return new StoredCredential(credential.UserName ?? string.Empty, password);
        }
        catch (CredentialStoreException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CredentialStoreException(connectionId, "read", innerException: ex);
        }
        finally
        {
            if (credentialPointer != IntPtr.Zero)
                CredFree(credentialPointer);
        }
    }

    public void Delete(string connectionId)
    {
        try
        {
            if (CredDelete(GetTargetName(connectionId), CredentialTypeGeneric, 0))
                return;

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
                throw new CredentialStoreException(connectionId, "delete", error);
        }
        catch (CredentialStoreException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CredentialStoreException(connectionId, "delete", innerException: ex);
        }
    }

    private static string GetTargetName(string connectionId) => TargetPrefix + connectionId;

    private static void ClearMemory(IntPtr buffer, int length)
    {
        for (var offset = 0; offset < length; offset++)
            Marshal.WriteByte(buffer, offset, 0);
    }

    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string targetName,
        uint type,
        uint flags,
        out IntPtr credentialPointer);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);
}
