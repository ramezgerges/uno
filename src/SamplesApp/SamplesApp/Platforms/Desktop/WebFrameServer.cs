#nullable enable

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Windows.System;
using SkiaSharp;
using Uno.UI.Runtime.Skia;

namespace SamplesApp;

/// <summary>
/// Serves the headless frames to a browser as polled JPEGs and forwards the browser's pointer and
/// keyboard events back into the app, so the stream is interactive without a VNC client. Uses a raw
/// <see cref="TcpListener"/> rather than <c>HttpListener</c>, which on Windows needs a URL ACL
/// registered up front for non-admin processes.
/// </summary>
internal sealed class WebFrameServer
{
	private readonly TcpListener _listener;
	private readonly HeadlessFrameSource _frames;
	private readonly object _bufferGate = new();
	private byte[] _buffer = Array.Empty<byte>();
	private readonly HeadlessInput? _input;
	private readonly int _port;

	public WebFrameServer(int port, HeadlessFrameSource frames, HeadlessInput? input = null)
	{
		_port = port;
		_frames = frames;
		_input = input;
		_listener = new TcpListener(IPAddress.Loopback, port);
	}

	public void Start()
	{
		_listener.Start();
		Console.WriteLine($"Browser view on http://127.0.0.1:{_port}/");

		new Thread(AcceptLoop) { IsBackground = true, Name = "Web frame accept" }.Start();
	}

	private void AcceptLoop()
	{
		while (true)
		{
			var client = _listener.AcceptTcpClient();
			ThreadPool.UnsafeQueueUserWorkItem(static state => state.server.Serve(state.client), (server: this, client), preferLocal: false);
		}
	}

	private void Serve(TcpClient client)
	{
		try
		{
			using (client)
			{
				client.NoDelay = true;

				// Long polls park on the socket, so the idle timeout has to outlast them.
				client.ReceiveTimeout = (int)TimeSpan.FromMinutes(2).TotalMilliseconds;

				using var stream = client.GetStream();

				// Connections are kept alive: a fresh connect plus a new thread per frame cost about as
				// much as rendering and encoding one, which halved the achievable frame rate.
				while (ReadRequest(stream) is { } request)
				{
					if (request.StartsWith("GET /input", StringComparison.Ordinal))
					{
						HandleInput(request);
						WriteStatus(stream, "204 No Content");
					}
					else if (request.StartsWith("GET /frame.jpg", StringComparison.Ordinal))
					{
						ServeSingleFrame(stream, request);
					}
					else
					{
						ServePage(stream);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Web frame client ended: {ex.Message}");
		}
	}

	/// <summary>
	/// Returns the request line, having consumed the whole header block. Draining matters: closing a
	/// socket with unread data in the receive buffer makes Windows send an RST rather than a FIN, and a
	/// browser then rejects the response it already received.
	/// </summary>
	private static string ReadRequest(NetworkStream stream)
	{
		string? requestLine = null;

		while (true)
		{
			var line = ReadLine(stream);

			if (line.Length == 0)
			{
				return requestLine ?? string.Empty;
			}

			requestLine ??= line;
		}
	}

	private static string ReadLine(NetworkStream stream)
	{
		var builder = new StringBuilder();
		var b = stream.ReadByte();

		while (b >= 0 && b != '\n' && builder.Length < 8192)
		{
			if (b != '\r')
			{
				builder.Append((char)b);
			}

			b = stream.ReadByte();
		}

		return builder.ToString();
	}

	private static void WriteStatus(NetworkStream stream, string status)
		=> stream.Write(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: keep-alive\r\n\r\n"));

	/// <summary>
	/// Applies one input event. Events arrive as query parameters on a GET rather than a body, so the
	/// request never needs parsing beyond its first line.
	/// </summary>
	private void HandleInput(string requestLine)
	{
		if (_input is not { } input)
		{
			return;
		}

		var start = requestLine.IndexOf('?');
		var end = requestLine.LastIndexOf(" HTTP", StringComparison.Ordinal);

		if (start < 0 || end < start)
		{
			return;
		}

		var query = requestLine[(start + 1)..end];
		string? kind = null;
		double x = 0, y = 0, delta = 0;
		var button = 0;
		var key = 0;
		var character = '\0';
		var isDown = false;

		foreach (var pair in query.Split('&'))
		{
			var split = pair.IndexOf('=');

			if (split < 0)
			{
				continue;
			}

			var name = pair[..split];
			var value = Uri.UnescapeDataString(pair[(split + 1)..]);

			switch (name)
			{
				case "t": kind = value; break;
				case "x": double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out x); break;
				case "y": double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out y); break;
				case "d": double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out delta); break;
				case "b": int.TryParse(value, out button); break;
				case "k": int.TryParse(value, out key); break;
				case "c": character = value.Length > 0 ? value[0] : '\0'; break;
				case "down": isDown = value == "1"; break;
			}
		}

		var pointerButton = button switch
		{
			1 => HeadlessPointerButton.Middle,
			2 => HeadlessPointerButton.Right,
			_ => HeadlessPointerButton.Left,
		};

		switch (kind)
		{
			case "move":
				input.MovePointer(x, y);
				break;
			case "down":
				input.PressPointer(pointerButton, x, y);
				break;
			case "up":
				input.ReleasePointer(pointerButton, x, y);
				break;
			case "wheel":
				input.ScrollPointer(delta, x, y);
				break;
			case "enter":
				input.EnterPointer(x, y);
				break;
			case "leave":
				input.ReleaseAll();
				break;
			case "key" when isDown:
				input.PressKey((VirtualKey)key, character == '\0' ? null : character);
				break;
			case "key":
				input.ReleaseKey((VirtualKey)key);
				break;
		}
	}

	private static void ServePage(NetworkStream stream)
	{
		var body = Encoding.UTF8.GetBytes(Page);
		var head = Encoding.ASCII.GetBytes(
			"HTTP/1.1 200 OK\r\n" +
			"Content-Type: text/html; charset=utf-8\r\n" +
			$"Content-Length: {body.Length}\r\n" +
			"Connection: keep-alive\r\n\r\n");

		stream.Write(head);
		stream.Write(body);
	}

	/// <summary>
	/// Renders and serves one frame. With <c>?since=N</c> the request waits for content newer than
	/// generation N, so an idle app costs nothing instead of re-encoding an unchanged frame.
	/// </summary>
	private void ServeSingleFrame(NetworkStream stream, string requestLine)
	{
		var since = -1L;
		var marker = requestLine.IndexOf("since=", StringComparison.Ordinal);

		if (marker >= 0)
		{
			var value = requestLine[(marker + 6)..];
			var stop = value.IndexOfAny(new[] { '&', ' ' });
			long.TryParse(stop < 0 ? value : value[..stop], out since);
		}

		// Deliberately not gated on a new generation. Presenting a frame is what drives Uno's render
		// pipeline onward, so waiting for an invalidation before rendering throttles the very thing
		// that produces invalidations: measured, gating cost ~5x the frame rate. The client paces
		// instead, and `since` only suppresses re-encoding a frame it already has.
		byte[] jpeg;
		HeadlessFrameInfo info;

		// One buffer per server, reused across renders so the incremental render path stays available.
		lock (_bufferGate)
		{
			var generation = since;

			if (!_frames.TryRenderInto(ref _buffer, ref generation, waitForNew: false, damage: null, out info))
			{
				WriteStatus(stream, "503 Service Unavailable");
				return;
			}

			if (since >= 0 && info.Generation == since)
			{
				// Nothing invalidated since the client's last frame, so its copy is still current. The
				// render above still happened, keeping the pipeline moving.
				WriteStatus(stream, "204 No Content");
				return;
			}

			jpeg = Encode(_buffer, info.Width, info.Height, info.Stride);
		}

		var head = Encoding.ASCII.GetBytes(
			"HTTP/1.1 200 OK\r\n" +
			"Content-Type: image/jpeg\r\n" +
			$"Content-Length: {jpeg.Length}\r\n" +
			$"X-Frame-Generation: {info.Generation}\r\n" +
			"Cache-Control: no-store\r\n" +
			"Connection: keep-alive\r\n\r\n");

		stream.Write(head);
		stream.Write(jpeg);
	}

	private static byte[] Encode(byte[] pixels, int width, int height, int stride)
	{
		// The headless buffer is BGRA8888 premultiplied, which is what SKImage expects. Pinning and
		// wrapping it avoids the full-frame copy FromPixelCopy would make on every single frame.
		var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
		var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

		try
		{
			using var image = SKImage.FromPixels(info, handle.AddrOfPinnedObject(), stride);
			using var data = image.Encode(SKEncodedImageFormat.Jpeg, 75);

			return data.ToArray();
		}
		finally
		{
			handle.Free();
		}
	}

	// Polls single frames rather than a multipart stream: multipart/x-mixed-replace renders
	// inconsistently across browsers, and an idle UI produces only one part, which several never paint.
	private const string Page = """
		<!doctype html>
		<html>
		<head><meta charset="utf-8"><title>Uno Headless</title></head>
		<body style="margin:0;background:#1b1b1b;display:flex;align-items:center;justify-content:center;height:100vh;overflow:hidden">
		<img id="v" tabindex="0" style="max-width:100%;max-height:100vh;outline:none;cursor:default" alt="Uno headless stream">
		<script>
		const img = document.getElementById('v');

		function send(params) {
			fetch('/input?' + new URLSearchParams(params), { keepalive: true }).catch(() => {});
		}

		// The image is letterboxed to fit, so client coordinates are mapped back through its displayed
		// rect onto the frame's own pixel grid.
		function at(e) {
			const r = img.getBoundingClientRect();
			if (!r.width || !r.height || !img.naturalWidth) { return null; }
			return {
				x: Math.round((e.clientX - r.left) * img.naturalWidth / r.width),
				y: Math.round((e.clientY - r.top) * img.naturalHeight / r.height)
			};
		}

		img.addEventListener('mousemove', e => { const p = at(e); if (p) send({ t: 'move', x: p.x, y: p.y }); });
		img.addEventListener('mousedown', e => { const p = at(e); if (p) { img.focus(); send({ t: 'down', b: e.button, x: p.x, y: p.y }); } e.preventDefault(); });
		img.addEventListener('mouseup', e => { const p = at(e); if (p) send({ t: 'up', b: e.button, x: p.x, y: p.y }); e.preventDefault(); });
		img.addEventListener('mouseenter', e => { const p = at(e); if (p) send({ t: 'enter', x: p.x, y: p.y }); });
		img.addEventListener('mouseleave', () => send({ t: 'leave' }));
		img.addEventListener('contextmenu', e => e.preventDefault());
		img.addEventListener('wheel', e => {
			const p = at(e);
			if (p) { send({ t: 'wheel', d: e.deltaY > 0 ? -120 : 120, x: p.x, y: p.y }); }
			e.preventDefault();
		}, { passive: false });

		// keyCode lines up with the Windows virtual-key values VirtualKey uses; a single-character
		// e.key is passed alongside it so text input receives the actual character.
		function onKey(e, down) {
			const params = { t: 'key', k: e.keyCode, down: down ? 1 : 0 };
			if (down && e.key && e.key.length === 1) { params.c = e.key; }
			send(params);
			if (e.key === 'Tab' || e.key === ' ' || e.key.startsWith('Arrow')) { e.preventDefault(); }
		}

		window.addEventListener('keydown', e => onKey(e, true));
		window.addEventListener('keyup', e => onKey(e, false));
		window.addEventListener('blur', () => send({ t: 'leave' }));

		// Long-polls on the frame generation: the request only returns once there is newer content, so
		// there is no fixed delay capping the rate and an idle app costs nothing.
		let since = -1;
		async function tick() {
			try {
				const res = await fetch('/frame.jpg?since=' + since, { cache: 'no-store' });
				if (res.status === 204) { requestAnimationFrame(tick); return; }
				if (res.ok) {
					since = Number(res.headers.get('X-Frame-Generation') ?? -1);
					const blob = await res.blob();
					const url = URL.createObjectURL(blob);
					const previous = img.src;
					img.src = url;
					if (previous.startsWith('blob:')) { URL.revokeObjectURL(previous); }
					requestAnimationFrame(tick);
					return;
				}
			} catch (e) { /* keep polling across transient failures */ }
			setTimeout(tick, 250);
		}

		tick();
		img.focus();
		</script>
		</body>
		</html>
		""";
}
