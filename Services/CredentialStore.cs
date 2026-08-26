using System;
using System.IO;

namespace SftpExplorerWinUI.Services;

public sealed record StoredCredential(string Username, string Password);

public interface ICredentialStore
{
    StoredCredential? Read(string connectionId);

    void Write(string connectionId, string username, string password);

    void Delete(string connectionId);
}

public sealed class CredentialStoreException : IOException
{
    public CredentialStoreException(string connectionId, string operation, int? nativeErrorCode = null, Exception? innerException = null)
        : base(CreateMessage(connectionId, operation, nativeErrorCode), innerException)
    {
        ConnectionId = connectionId;
        Operation = operation;
        NativeErrorCode = nativeErrorCode;
    }

    public string ConnectionId { get; }

    public string Operation { get; }

    public int? NativeErrorCode { get; }

    private static string CreateMessage(string connectionId, string operation, int? nativeErrorCode)
    {
        var errorSuffix = nativeErrorCode.HasValue ? $" Native error: {nativeErrorCode.Value}." : string.Empty;
        return $"Windows Credential Manager could not {operation} the credential for connection '{connectionId}'.{errorSuffix}";
    }
}

public sealed class ConnectionPersistenceException : IOException
{
    public ConnectionPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
