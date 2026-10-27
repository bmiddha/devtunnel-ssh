namespace Dtssh.Auth;

// The service managers need only this exception from the otherwise unrelated
// devtunnel CLI implementation. No CLI or service operations run in unit tests.
internal sealed class DtsshException(string message) : Exception(message);
