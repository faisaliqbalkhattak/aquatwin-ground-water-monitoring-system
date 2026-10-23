using System;
#if UNITY_WEBGL && !UNITY_EDITOR
using UnityEngine;
using System.Runtime.InteropServices;
#else
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
#endif

namespace AquaTwin
{
    public readonly struct MqttSocketEvent
    {
        public readonly string Kind, Message;
        public readonly byte[] Data;
        public MqttSocketEvent(string kind, string message = null, byte[] data = null)
        { Kind = kind; Message = message; Data = data; }
    }

    /// <summary>Browser APIs in WebGL; ClientWebSocket only in Editor/desktop. Polled on Unity's main thread.</summary>
    public sealed class MqttSocketTransport : IDisposable
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern int AQ_WS_Open(string url);
        [DllImport("__Internal")] private static extern void AQ_WS_Send(int id, byte[] bytes, int length);
        [DllImport("__Internal")] private static extern IntPtr AQ_WS_Poll(int id);
        [DllImport("__Internal")] private static extern void AQ_WS_Release(IntPtr pointer);
        [DllImport("__Internal")] private static extern void AQ_WS_Close(int id);
        [Serializable, UnityEngine.Scripting.Preserve]
        private sealed class BrowserEvent { public string kind = null, message = null, data = null; }
        private readonly int id;
        private bool disposed;
        public MqttSocketTransport(Uri endpoint) { id = AQ_WS_Open(endpoint.AbsoluteUri); }
        public void Send(byte[] bytes) { if (!disposed) AQ_WS_Send(id, bytes, bytes.Length); }
        public bool Poll(out MqttSocketEvent value)
        {
            value = default;
            if (disposed) return false;
            IntPtr pointer = AQ_WS_Poll(id);
            if (pointer == IntPtr.Zero) return false;
            try
            {
                var item = JsonUtility.FromJson<BrowserEvent>(Marshal.PtrToStringAnsi(pointer));
                value = new MqttSocketEvent(item.kind, item.message,
                    item.kind == "data" ? Convert.FromBase64String(item.data) : null);
                return true;
            }
            finally { AQ_WS_Release(pointer); }
        }
        public void Dispose() { if (!disposed) { disposed = true; AQ_WS_Close(id); } }
#else
        private readonly ClientWebSocket socket = new();
        private readonly CancellationTokenSource cancellation = new();
        private readonly SemaphoreSlim sends = new(1, 1);
        private readonly ConcurrentQueue<MqttSocketEvent> events = new();
        private volatile bool disposed;
        public MqttSocketTransport(Uri endpoint) { socket.Options.AddSubProtocol("mqtt"); Run(endpoint); }
        private void Enqueue(MqttSocketEvent item)
        {
            if (disposed) return;
            if (events.Count < 256) events.Enqueue(item);
            else { events.Enqueue(new MqttSocketEvent("error", "Incoming message queue exceeded its limit. Reduce publish rate.")); socket.Abort(); }
        }
        private async void Run(Uri endpoint)
        {
            try
            {
                await socket.ConnectAsync(endpoint, cancellation.Token).ConfigureAwait(false);
                Enqueue(new MqttSocketEvent("open"));
                var buffer = new byte[8192];
                while (!cancellation.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    { Enqueue(new MqttSocketEvent("close", "WebSocket closed (" + result.CloseStatus + ").")); break; }
                    if (result.MessageType != WebSocketMessageType.Binary) throw new InvalidOperationException("MQTT requires binary WebSocket frames.");
                    var bytes = new byte[result.Count]; Array.Copy(buffer, bytes, result.Count);
                    Enqueue(new MqttSocketEvent("data", data: bytes));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Enqueue(new MqttSocketEvent("error", "WebSocket connection failed: " + Describe(exception))); }
            finally { socket.Dispose(); }
        }
        public async void Send(byte[] bytes)
        {
            try
            {
                await sends.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try { await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Binary, true, cancellation.Token).ConfigureAwait(false); }
                finally { sends.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { Enqueue(new MqttSocketEvent("error", "WebSocket send failed: " + Describe(exception))); }
        }
        private static string Describe(Exception exception)
        {
            var cause = exception.GetBaseException();
            return cause == exception ? exception.Message : exception.Message + " · " + cause.GetType().Name + ": " + cause.Message;
        }
        public bool Poll(out MqttSocketEvent value) => events.TryDequeue(out value);
        public void Dispose()
        {
            if (disposed) return; disposed = true; cancellation.Cancel();
            try { socket.Abort(); } catch (ObjectDisposedException) { }
        }
#endif
    }
}
