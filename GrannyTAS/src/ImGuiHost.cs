using System;
using ImGuiNET;
using MelonLoader;
using UnityEngine;

namespace GrannyTAS
{
    /// <summary>
    /// Runs real Dear ImGui inside the game.
    ///
    /// Unity has no ImGui backend, so this is one: it owns the ImGui context,
    /// feeds it input, and rasterises the draw lists ImGui produces using
    /// Unity's immediate-mode <see cref="GL"/> API during <c>OnGUI</c>'s repaint
    /// pass.
    ///
    /// Three constraints shaped the renderer:
    ///
    /// * **Shaders cannot be compiled at runtime.** A built player only contains
    ///   shaders that were included at build time, so the material has to be
    ///   found among the game's own. <c>UI/Default</c> is the target — it takes
    ///   a texture and vertex colours, which is exactly ImGui's vertex format,
    ///   and any game with a Canvas has it. Fallbacks follow in
    ///   <see cref="FindMaterial"/>.
    /// * **Unity's GL wrapper exposes no scissor rectangle.** ImGui emits a clip
    ///   rect per draw command and expects the backend to honour it; ignoring
    ///   them lets any item straddling a window's edge — the last widget in a
    ///   size-constrained or scrolled window — draw straight through the border.
    ///   <see cref="DrawData"/> therefore clips on the CPU instead.
    /// * **Anything here can fail on someone else's machine.** A missing
    ///   <c>cimgui.dll</c>, a stripped shader, an unexpected ImGui version.
    ///   Every entry point is guarded and sets <see cref="Failed"/>, which makes
    ///   the mod fall back to the legacy IMGUI overlay rather than leaving the
    ///   user with no interface at all.
    ///
    /// Mouse and keyboard input are read from Unity hardware while the virtual
    /// input shim is bypassed, so replay naming never consumes macro input.
    /// </summary>
    public sealed class ImGuiHost
    {
        private readonly MelonLogger.Instance _log;

        private IntPtr _context;
        private Texture2D _fontAtlas;
        private Material _material;
        private bool _frameStarted;

        /// <summary>Set once anything goes wrong; the mod then uses the legacy overlay.</summary>
        public bool Failed { get; private set; }

        public bool Ready => _context != IntPtr.Zero && !Failed;

        public bool WantsTextInput
        {
            get
            {
                if (!Ready || !Visible) return false;
                ImGui.SetCurrentContext(_context);
                return ImGui.GetIO().WantTextInput;
            }
        }

        /// <summary>Whether the panel is showing. Hidden by default so it never blocks play.</summary>
        public bool Visible { get; set; }

        public ImGuiHost(MelonLogger.Instance log) => _log = log;

        public unsafe bool Initialize()
        {
            if (Failed) return false;
            if (_context != IntPtr.Zero) return true;

            try
            {
                _context = ImGui.CreateContext();
                ImGui.SetCurrentContext(_context);

                var io = ImGui.GetIO();
                io.DisplaySize = new System.Numerics.Vector2(Screen.width, Screen.height);
                io.DisplayFramebufferScale = new System.Numerics.Vector2(1f, 1f);

                // No .ini persistence: the file would land in the game directory
                // and ImGui writes it on its own schedule.
                io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
                io.NativePtr->IniFilename = null;

                ImGui.StyleColorsDark();

                if (!BuildFontAtlas()) return Fail("could not build the font atlas");
                if (!FindMaterial()) return Fail("no usable shader found in this build");

                _log.Msg($"Dear ImGui {ImGui.GetVersion()} initialised ({_fontAtlas.width}x{_fontAtlas.height} atlas, shader '{_material.shader.name}').");
                return true;
            }
            catch (DllNotFoundException e)
            {
                return Fail($"cimgui.dll not found next to the game executable ({e.Message})");
            }
            catch (Exception e)
            {
                return Fail(e.Message);
            }
        }

        private bool Fail(string why)
        {
            Failed = true;
            _log.Warning($"Dear ImGui unavailable ({why}). Falling back to the legacy overlay.");
            return false;
        }

        public void ReportFailure(string why) => Fail(why);

        private unsafe bool BuildFontAtlas()
        {
            var io = ImGui.GetIO();
            io.Fonts.AddFontDefault();
            io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out var width, out var height, out _);
            if (pixels == null || width <= 0 || height <= 0) return false;

            _fontAtlas = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            var bytes = width * height * 4;
            var managed = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)pixels, managed, 0, bytes);

            // Unity uploads raw texture data bottom-row-first while ImGui's atlas
            // is top-row-first, so flip here rather than inverting every V
            // coordinate at draw time.
            var stride = width * 4;
            var flipped = new byte[bytes];
            for (var row = 0; row < height; row++)
                Buffer.BlockCopy(managed, row * stride, flipped, (height - 1 - row) * stride, stride);

            _fontAtlas.LoadRawTextureData(flipped);
            _fontAtlas.Apply(false, false);

            io.Fonts.SetTexID((IntPtr)1);
            io.Fonts.ClearTexData();
            return true;
        }

        private bool FindMaterial()
        {
            // Ordered by how well each handles textured, vertex-coloured, alpha-
            // blended geometry. UI/Default is present whenever the game uses uGUI.
            foreach (var name in new[] { "UI/Default", "Sprites/Default", "Hidden/Internal-GUITexture", "Unlit/Transparent" })
            {
                var shader = Shader.Find(name);
                if (shader == null) continue;

                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                return true;
            }
            return false;
        }

        /// <summary>Begin a frame. Call once per rendered frame, before drawing any UI.</summary>
        public void NewFrame(float deltaTime)
        {
            if (!Ready || _frameStarted) return;

            try
            {
                ImGui.SetCurrentContext(_context);
                var io = ImGui.GetIO();

                io.DisplaySize = new System.Numerics.Vector2(Screen.width, Screen.height);
                // ImGui asserts on a non-positive delta, and a paused game has one.
                io.DeltaTime = deltaTime > 0f ? deltaTime : 1f / 60f;

                FeedInput(io);

                ImGui.NewFrame();
                _frameStarted = true;
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        private static void FeedInput(ImGuiIOPtr io)
        {
            // Read hardware directly: the virtual input layer exists to lie to
            // the game, and the mod's own UI must not be lied to.
            var wasBypassing = VirtualInput.Bypass;
            VirtualInput.Bypass = true;
            try
            {
                var m = Input.mousePosition;
                // Unity's origin is bottom-left, ImGui's is top-left.
                io.AddMousePosEvent(m.x, Screen.height - m.y);

                for (var b = 0; b < 3; b++) io.AddMouseButtonEvent(b, Input.GetMouseButton(b));

                var wheel = Input.GetAxisRaw("Mouse ScrollWheel");
                if (Mathf.Abs(wheel) > 0.0001f) io.AddMouseWheelEvent(0f, wheel * 10f);

                FeedKeyboard(io);
            }
            finally
            {
                VirtualInput.Bypass = wasBypassing;
            }
        }

        private static void FeedKeyboard(ImGuiIOPtr io)
        {
            Key(io, ImGuiKey.Backspace, KeyCode.Backspace);
            Key(io, ImGuiKey.Delete, KeyCode.Delete);
            Key(io, ImGuiKey.LeftArrow, KeyCode.LeftArrow);
            Key(io, ImGuiKey.RightArrow, KeyCode.RightArrow);
            Key(io, ImGuiKey.UpArrow, KeyCode.UpArrow);
            Key(io, ImGuiKey.DownArrow, KeyCode.DownArrow);
            Key(io, ImGuiKey.Home, KeyCode.Home);
            Key(io, ImGuiKey.End, KeyCode.End);
            Key(io, ImGuiKey.Enter, KeyCode.Return);
            Key(io, ImGuiKey.KeypadEnter, KeyCode.KeypadEnter);
            Key(io, ImGuiKey.Escape, KeyCode.Escape);
            Key(io, ImGuiKey.A, KeyCode.A);
            Key(io, ImGuiKey.C, KeyCode.C);
            Key(io, ImGuiKey.V, KeyCode.V);
            Key(io, ImGuiKey.X, KeyCode.X);
            Key(io, ImGuiKey.Y, KeyCode.Y);
            Key(io, ImGuiKey.Z, KeyCode.Z);

            io.AddKeyEvent(ImGuiKey.ModCtrl,
                Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));
            io.AddKeyEvent(ImGuiKey.ModShift,
                Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            io.AddKeyEvent(ImGuiKey.ModAlt,
                Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));

            var typed = Input.inputString;
            for (var i = 0; i < typed.Length; i++)
            {
                var c = typed[i];
                if (!char.IsControl(c)) io.AddInputCharacter(c);
            }
        }

        private static void Key(ImGuiIOPtr io, ImGuiKey imgui, KeyCode unity) =>
            io.AddKeyEvent(imgui, Input.GetKey(unity));

        /// <summary>
        /// End the frame and draw it. Must run inside <c>OnGUI</c> on a repaint
        /// event — that is the only point where Unity lets arbitrary GL run over
        /// the finished scene.
        /// </summary>
        public void Render()
        {
            if (!Ready || !_frameStarted) return;

            try
            {
                ImGui.SetCurrentContext(_context);
                ImGui.Render();
                DrawData(ImGui.GetDrawData());
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
            finally
            {
                _frameStarted = false;
            }
        }

        /// <summary>
        /// End the frame without drawing, for when rendering is not possible —
        /// most often TasWindow.Draw throwing partway through a window.
        ///
        /// Uses <c>ImGui.EndFrame()</c> rather than <c>ImGui.Render()</c>: Render
        /// expects the window stack it started with to be balanced, and a throw
        /// between <c>ImGui.Begin</c> and <c>ImGui.End</c> leaves it open, which
        /// trips Dear ImGui's end-of-frame assert — a native abort, not a
        /// catchable exception. EndFrame tolerates the same imbalance because it
        /// is the half of the pair that does not walk the draw-command stack.
        /// Also guarded end to end, and <see cref="_frameStarted"/> is cleared
        /// unconditionally, so a failure here still leaves the state machine
        /// able to start a fresh frame next time rather than wedging it.
        /// </summary>
        public void Discard()
        {
            if (!Ready || !_frameStarted) return;
            try { ImGui.EndFrame(); } catch { }
            _frameStarted = false;
        }

        /// <summary>One vertex mid-clip. Colour is unpacked so it can be interpolated.</summary>
        private struct ClipVert
        {
            public float X, Y, U, V, R, G, B, A;
        }

        // Clipping a triangle against four edges adds at most one vertex per
        // edge, so seven is the true bound; eight keeps the arithmetic obvious.
        private const int MaxPolyVerts = 8;

        private readonly ClipVert[] _polyA = new ClipVert[MaxPolyVerts];
        private readonly ClipVert[] _polyB = new ClipVert[MaxPolyVerts];

        private unsafe void DrawData(ImDrawDataPtr data)
        {
            if (data.NativePtr == null || data.CmdListsCount == 0) return;

            GL.PushMatrix();
            try
            {
                // Pixel-space projection with Y increasing downward, matching ImGui.
                GL.LoadPixelMatrix(0f, Screen.width, Screen.height, 0f);

                _material.mainTexture = _fontAtlas;
                _material.SetPass(0);

                var origin = data.DisplayPos;

                for (var n = 0; n < data.CmdListsCount; n++)
                {
                    var cmdList = data.CmdLists[n];
                    var vtx = cmdList.VtxBuffer;
                    var idx = cmdList.IdxBuffer;

                    for (var c = 0; c < cmdList.CmdBuffer.Size; c++)
                    {
                        var cmd = cmdList.CmdBuffer[c];
                        if (cmd.ElemCount == 0) continue;

                        // Clip rects share the vertices' space once DisplayPos is
                        // subtracted. Clamping to the framebuffer keeps ImGui's
                        // "everything" rect (effectively ±FLT_MAX) from making the
                        // inside-test below overflow.
                        var clipL = Mathf.Max(cmd.ClipRect.X - origin.X, 0f);
                        var clipT = Mathf.Max(cmd.ClipRect.Y - origin.Y, 0f);
                        var clipR = Mathf.Min(cmd.ClipRect.Z - origin.X, Screen.width);
                        var clipB = Mathf.Min(cmd.ClipRect.W - origin.Y, Screen.height);
                        if (clipR <= clipL || clipB <= clipT) continue;

                        GL.Begin(GL.TRIANGLES);
                        try
                        {
                            var end = (int)(cmd.IdxOffset + cmd.ElemCount);
                            for (var i = (int)cmd.IdxOffset; i + 2 < end; i += 3)
                            {
                                var v0 = vtx[idx[i] + (int)cmd.VtxOffset];
                                var v1 = vtx[idx[i + 1] + (int)cmd.VtxOffset];
                                var v2 = vtx[idx[i + 2] + (int)cmd.VtxOffset];

                                // Nearly every triangle sits wholly inside its clip
                                // rect; those go straight through, so the clipper only
                                // costs anything at a window's edge.
                                if (Inside(v0, clipL, clipT, clipR, clipB) &&
                                    Inside(v1, clipL, clipT, clipR, clipB) &&
                                    Inside(v2, clipL, clipT, clipR, clipB))
                                {
                                    Emit(v0);
                                    Emit(v1);
                                    Emit(v2);
                                    continue;
                                }

                                _polyA[0] = ToClipVert(v0);
                                _polyA[1] = ToClipVert(v1);
                                _polyA[2] = ToClipVert(v2);

                                var count = ClipPolygon(3, clipL, clipT, clipR, clipB);
                                if (count < 3) continue;

                                // Sutherland–Hodgman leaves a convex polygon; fan it.
                                for (var t = 1; t < count - 1; t++)
                                {
                                    Emit(_polyA[0]);
                                    Emit(_polyA[t]);
                                    Emit(_polyA[t + 1]);
                                }
                            }

                        }
                        finally { GL.End(); }
                    }
                }

            }
            finally { GL.PopMatrix(); }
        }

        private static bool Inside(ImDrawVertPtr v, float l, float t, float r, float b)
            => v.pos.X >= l && v.pos.X <= r && v.pos.Y >= t && v.pos.Y <= b;

        private static ClipVert ToClipVert(ImDrawVertPtr v)
        {
            var col = v.col;
            return new ClipVert
            {
                X = v.pos.X,
                Y = v.pos.Y,
                U = v.uv.X,
                V = v.uv.Y,
                R = (col & 0xFF) / 255f,
                G = ((col >> 8) & 0xFF) / 255f,
                B = ((col >> 16) & 0xFF) / 255f,
                A = ((col >> 24) & 0xFF) / 255f,
            };
        }

        /// <summary>
        /// Clips the polygon held in <see cref="_polyA"/> against the four edges
        /// of the clip rect, leaving the result back in <see cref="_polyA"/>.
        /// Returns the vertex count — zero when nothing survives.
        /// </summary>
        private int ClipPolygon(int count, float l, float t, float r, float b)
        {
            count = ClipEdge(_polyA, count, _polyB, axisX: true, limit: l, keepGreater: true);
            count = ClipEdge(_polyB, count, _polyA, axisX: true, limit: r, keepGreater: false);
            count = ClipEdge(_polyA, count, _polyB, axisX: false, limit: t, keepGreater: true);
            count = ClipEdge(_polyB, count, _polyA, axisX: false, limit: b, keepGreater: false);
            return count;
        }

        private static int ClipEdge(ClipVert[] src, int count, ClipVert[] dst, bool axisX, float limit, bool keepGreater)
        {
            if (count == 0) return 0;

            var written = 0;
            var prev = src[count - 1];
            var prevDist = Distance(prev, axisX, limit, keepGreater);

            for (var i = 0; i < count; i++)
            {
                var cur = src[i];
                var curDist = Distance(cur, axisX, limit, keepGreater);

                // Crossing the edge in either direction contributes the
                // intersection; being on the inside contributes the vertex.
                if ((curDist >= 0f) != (prevDist >= 0f) && written < MaxPolyVerts)
                    dst[written++] = Lerp(prev, cur, prevDist / (prevDist - curDist));

                if (curDist >= 0f && written < MaxPolyVerts)
                    dst[written++] = cur;

                prev = cur;
                prevDist = curDist;
            }

            return written;
        }

        /// <summary>Signed distance from the edge; non-negative means keep.</summary>
        private static float Distance(ClipVert v, bool axisX, float limit, bool keepGreater)
        {
            var value = axisX ? v.X : v.Y;
            return keepGreater ? value - limit : limit - value;
        }

        private static ClipVert Lerp(ClipVert a, ClipVert b, float t) => new ClipVert
        {
            X = a.X + (b.X - a.X) * t,
            Y = a.Y + (b.Y - a.Y) * t,
            U = a.U + (b.U - a.U) * t,
            V = a.V + (b.V - a.V) * t,
            R = a.R + (b.R - a.R) * t,
            G = a.G + (b.G - a.G) * t,
            B = a.B + (b.B - a.B) * t,
            A = a.A + (b.A - a.A) * t,
        };

        private static void Emit(ImDrawVertPtr v)
        {
            var col = v.col;
            GL.Color(new Color32(
                (byte)(col & 0xFF),
                (byte)((col >> 8) & 0xFF),
                (byte)((col >> 16) & 0xFF),
                (byte)((col >> 24) & 0xFF)));

            // V is flipped to match the atlas upload above.
            GL.TexCoord2(v.uv.X, 1f - v.uv.Y);
            GL.Vertex3(v.pos.X, v.pos.Y, 0f);
        }

        private static void Emit(ClipVert v)
        {
            GL.Color(new Color(v.R, v.G, v.B, v.A));
            GL.TexCoord2(v.U, 1f - v.V);
            GL.Vertex3(v.X, v.Y, 0f);
        }

        public void Shutdown()
        {
            try
            {
                if (_context != IntPtr.Zero)
                {
                    ImGui.DestroyContext(_context);
                    _context = IntPtr.Zero;
                }
            }
            catch { }

            if (_fontAtlas != null) UnityEngine.Object.Destroy(_fontAtlas);
            if (_material != null) UnityEngine.Object.Destroy(_material);
        }
    }
}
