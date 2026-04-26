window.MeshViewer = (() => {
    function init(canvasId, vertices, indices) {
        const canvas = document.getElementById(canvasId);
        if (!canvas) { console.warn('[MeshViewer] canvas not found:', canvasId); return; }

        const gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
        if (!gl) { console.warn('[MeshViewer] WebGL not supported'); return; }

        const ext32 = gl.getExtension('OES_element_index_uint');
        const useUint32 = !!ext32;

        const vsSource = `
            attribute vec3 aPos;
            attribute vec3 aNormal;
            uniform mat4 uMVP;
            uniform mat4 uModel;
            varying vec3 vNormal;
            varying vec3 vWorldPos;
            void main() {
                vec4 worldPos = uModel * vec4(aPos, 1.0);
                vWorldPos = worldPos.xyz;
                vNormal = mat3(uModel) * aNormal;
                gl_Position = uMVP * vec4(aPos, 1.0);
            }
        `;
        const fsSource = `
            precision mediump float;
            varying vec3 vNormal;
            varying vec3 vWorldPos;
            uniform vec3 uLightDir;
            void main() {
                vec3 n = normalize(vNormal);
                float diff = max(dot(n, normalize(uLightDir)), 0.0);
                float rim = pow(1.0 - max(dot(n, vec3(0.0, 0.0, 1.0)), 0.0), 2.0) * 0.3;
                vec3 col = vec3(0.25, 0.55, 0.85);
                vec3 ambient = col * 0.25;
                vec3 light = col * diff * 0.75 + ambient + rim;
                gl_FragColor = vec4(light, 1.0);
            }
        `;
        const vsWire = `
            attribute vec3 aPos;
            uniform mat4 uMVP;
            void main() { gl_Position = uMVP * vec4(aPos, 1.0); }
        `;
        const fsWire = `
            precision mediump float;
            void main() { gl_FragColor = vec4(0.15, 0.35, 0.6, 1.0); }
        `;

        function compileShader(src, type) {
            const s = gl.createShader(type);
            gl.shaderSource(s, src);
            gl.compileShader(s);
            if (!gl.getShaderParameter(s, gl.COMPILE_STATUS))
                console.warn('[MeshViewer] shader error:', gl.getShaderInfoLog(s));
            return s;
        }
        function makeProgram(vs, fs) {
            const p = gl.createProgram();
            gl.attachShader(p, compileShader(vs, gl.VERTEX_SHADER));
            gl.attachShader(p, compileShader(fs, gl.FRAGMENT_SHADER));
            gl.linkProgram(p);
            if (!gl.getProgramParameter(p, gl.LINK_STATUS))
                console.warn('[MeshViewer] program link error:', gl.getProgramInfoLog(p));
            return p;
        }

        const solidProg = makeProgram(vsSource, fsSource);
        const wireProg  = makeProgram(vsWire,   fsWire);

        const verts = new Float32Array(vertices);
        const vertCount = verts.length / 3;

        const idx = (vertCount > 65535 && useUint32)
            ? new Uint32Array(indices)
            : new Uint16Array(indices);
        const GL_IDX_TYPE = (vertCount > 65535 && useUint32) ? gl.UNSIGNED_INT : gl.UNSIGNED_SHORT;

        const normals = new Float32Array(verts.length);
        for (let i = 0; i < idx.length; i += 3) {
            const i0 = idx[i], i1 = idx[i+1], i2 = idx[i+2];
            const ax = verts[i0*3],   ay = verts[i0*3+1], az = verts[i0*3+2];
            const bx = verts[i1*3],   by = verts[i1*3+1], bz = verts[i1*3+2];
            const cx = verts[i2*3],   cy = verts[i2*3+1], cz = verts[i2*3+2];
            const ux = bx-ax, uy = by-ay, uz = bz-az;
            const vx = cx-ax, vy = cy-ay, vz = cz-az;
            const nx = uy*vz - uz*vy, ny = uz*vx - ux*vz, nz = ux*vy - uy*vx;
            for (const vi of [i0, i1, i2]) {
                normals[vi*3]   += nx;
                normals[vi*3+1] += ny;
                normals[vi*3+2] += nz;
            }
        }
        for (let i = 0; i < vertCount; i++) {
            const nx = normals[i*3], ny = normals[i*3+1], nz = normals[i*3+2];
            const len = Math.sqrt(nx*nx + ny*ny + nz*nz) || 1;
            normals[i*3] /= len; normals[i*3+1] /= len; normals[i*3+2] /= len;
        }

        const edgeSet = new Set();
        const wireIdxArr = [];
        for (let i = 0; i < idx.length; i += 3) {
            const tri = [idx[i], idx[i+1], idx[i+2]];
            for (let e = 0; e < 3; e++) {
                const a = tri[e], b = tri[(e+1)%3];
                const key = a < b ? `${a}_${b}` : `${b}_${a}`;
                if (!edgeSet.has(key)) { edgeSet.add(key); wireIdxArr.push(a, b); }
            }
        }
        const wireIdx = (vertCount > 65535 && useUint32)
            ? new Uint32Array(wireIdxArr)
            : new Uint16Array(wireIdxArr);

        const posBuf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, posBuf);
        gl.bufferData(gl.ARRAY_BUFFER, verts, gl.STATIC_DRAW);

        const normBuf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, normBuf);
        gl.bufferData(gl.ARRAY_BUFFER, normals, gl.STATIC_DRAW);

        const idxBuf = gl.createBuffer();
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, idxBuf);
        gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, idx, gl.STATIC_DRAW);

        const wireIdxBuf = gl.createBuffer();
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, wireIdxBuf);
        gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, wireIdx, gl.STATIC_DRAW);

        let minX=Infinity,minY=Infinity,minZ=Infinity;
        let maxX=-Infinity,maxY=-Infinity,maxZ=-Infinity;
        for (let i = 0; i < verts.length; i += 3) {
            minX=Math.min(minX,verts[i]);   maxX=Math.max(maxX,verts[i]);
            minY=Math.min(minY,verts[i+1]); maxY=Math.max(maxY,verts[i+1]);
            minZ=Math.min(minZ,verts[i+2]); maxZ=Math.max(maxZ,verts[i+2]);
        }
        const cx=(minX+maxX)/2, cy=(minY+maxY)/2, cz=(minZ+maxZ)/2;
        const scl = 2.0 / Math.max(maxX-minX, maxY-minY, maxZ-minZ, 0.001);

        let rotX=0.4, rotY=-0.6, dist=3.5;
        let dragging=false, lastX=0, lastY=0;

        canvas.addEventListener('mousedown', e => { dragging=true; lastX=e.clientX; lastY=e.clientY; });
        window.addEventListener('mouseup',   () => { dragging=false; });
        window.addEventListener('mousemove', e => {
            if (!dragging) return;
            rotY += (e.clientX-lastX)*0.01;
            rotX += (e.clientY-lastY)*0.01;
            lastX=e.clientX; lastY=e.clientY;
            render();
        });
        canvas.addEventListener('wheel', e => {
            e.preventDefault();
            dist = Math.max(1.5, Math.min(10, dist + e.deltaY*0.005));
            render();
        }, { passive: false });

        let lastTouchDist=0;
        canvas.addEventListener('touchstart', e => {
            if (e.touches.length===1) { dragging=true; lastX=e.touches[0].clientX; lastY=e.touches[0].clientY; }
            else if (e.touches.length===2) lastTouchDist=Math.hypot(e.touches[0].clientX-e.touches[1].clientX, e.touches[0].clientY-e.touches[1].clientY);
        });
        canvas.addEventListener('touchend', () => { dragging=false; });
        canvas.addEventListener('touchmove', e => {
            e.preventDefault();
            if (e.touches.length===1 && dragging) {
                rotY += (e.touches[0].clientX-lastX)*0.01;
                rotX += (e.touches[0].clientY-lastY)*0.01;
                lastX=e.touches[0].clientX; lastY=e.touches[0].clientY;
                render();
            } else if (e.touches.length===2) {
                const d=Math.hypot(e.touches[0].clientX-e.touches[1].clientX, e.touches[0].clientY-e.touches[1].clientY);
                dist=Math.max(1.5,Math.min(10,dist-(d-lastTouchDist)*0.01));
                lastTouchDist=d; render();
            }
        }, { passive: false });

        function identity() { const m=new Float32Array(16); m[0]=m[5]=m[10]=m[15]=1; return m; }
        function mul(a, b) {
            const r=new Float32Array(16);
            for (let c=0;c<4;c++) for (let row=0;row<4;row++)
                for (let k=0;k<4;k++) r[c*4+row]+=a[k*4+row]*b[c*4+k];
            return r;
        }
        function perspective(fov,asp,near,far) {
            const f=1/Math.tan(fov/2), m=new Float32Array(16);
            m[0]=f/asp; m[5]=f; m[10]=(far+near)/(near-far); m[11]=-1;
            m[14]=(2*far*near)/(near-far); return m;
        }
        function rotX_(a) { const m=identity(),c=Math.cos(a),s=Math.sin(a); m[5]=c;m[6]=s;m[9]=-s;m[10]=c; return m; }
        function rotY_(a) { const m=identity(),c=Math.cos(a),s=Math.sin(a); m[0]=c;m[2]=-s;m[8]=s;m[10]=c; return m; }
        function trans(x,y,z) { const m=identity(); m[12]=x;m[13]=y;m[14]=z; return m; }
        function scaleM(s) { const m=identity(); m[0]=m[5]=m[10]=s; return m; }

        function render() {
            const W=canvas.width, H=canvas.height;
            gl.viewport(0,0,W,H);
            gl.clearColor(0.1,0.1,0.1,1);
            gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);
            gl.enable(gl.DEPTH_TEST);

            const proj  = perspective(0.8, W/H, 0.1, 100);
            const view  = trans(0,0,-dist);
            const model = mul(mul(rotX_(rotX), rotY_(rotY)), mul(scaleM(scl), trans(-cx,-cy,-cz)));
            const mvp   = mul(proj, mul(view, model));

            gl.useProgram(solidProg);
            gl.uniformMatrix4fv(gl.getUniformLocation(solidProg,'uMVP'),   false, mvp);
            gl.uniformMatrix4fv(gl.getUniformLocation(solidProg,'uModel'), false, model);
            gl.uniform3f(gl.getUniformLocation(solidProg,'uLightDir'), 0.6, 1.0, 0.8);

            const posLoc  = gl.getAttribLocation(solidProg,'aPos');
            gl.bindBuffer(gl.ARRAY_BUFFER, posBuf);
            gl.enableVertexAttribArray(posLoc);
            gl.vertexAttribPointer(posLoc,3,gl.FLOAT,false,0,0);

            const normLoc = gl.getAttribLocation(solidProg,'aNormal');
            gl.bindBuffer(gl.ARRAY_BUFFER, normBuf);
            gl.enableVertexAttribArray(normLoc);
            gl.vertexAttribPointer(normLoc,3,gl.FLOAT,false,0,0);

            gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, idxBuf);
            gl.drawElements(gl.TRIANGLES, idx.length, GL_IDX_TYPE, 0);

            gl.enable(gl.POLYGON_OFFSET_FILL);
            gl.polygonOffset(-1,-1);

            gl.useProgram(wireProg);
            gl.uniformMatrix4fv(gl.getUniformLocation(wireProg,'uMVP'), false, mvp);

            const wPos = gl.getAttribLocation(wireProg,'aPos');
            gl.bindBuffer(gl.ARRAY_BUFFER, posBuf);
            gl.enableVertexAttribArray(wPos);
            gl.vertexAttribPointer(wPos,3,gl.FLOAT,false,0,0);

            gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, wireIdxBuf);
            gl.drawElements(gl.LINES, wireIdx.length, GL_IDX_TYPE, 0);

            gl.disable(gl.POLYGON_OFFSET_FILL);
        }

        render();
    }

    // ── Toast notification ────────────────────────────────────────────────────
    function _showToast(msg, isError) {
        isError = !!isError;
        const existing = document.getElementById('mv-toast');
        if (existing) existing.remove();

        const el = document.createElement('div');
        el.id = 'mv-toast';

        const icon = document.createElement('span');
        icon.textContent = isError ? '\u2715' : '\u2713';
        icon.style.cssText =
            'display:inline-flex;align-items:center;justify-content:center;' +
            'width:18px;height:18px;border-radius:50%;' +
            'background:rgba(255,255,255,0.25);font-size:11px;flex-shrink:0;';

        const label = document.createElement('span');
        label.textContent = msg;

        el.appendChild(icon);
        el.appendChild(label);
        el.style.cssText =
            'position:fixed;bottom:24px;right:20px;' +
            'background:' + (isError ? '#c0392b' : '#00b894') + ';' +
            'color:#fff;padding:9px 14px;border-radius:6px;' +
            'font-family:Segoe UI,sans-serif;font-size:13px;font-weight:500;' +
            'box-shadow:0 4px 16px rgba(0,0,0,0.45);' +
            'z-index:99999;display:flex;align-items:center;gap:8px;' +
            'max-width:320px;pointer-events:none;opacity:1;' +
            'transition:opacity 0.35s ease;';

        document.body.appendChild(el);
        setTimeout(function() {
            el.style.opacity = '0';
            setTimeout(function() { if (el.parentNode) el.remove(); }, 380);
        }, 3000);
    }

    // ── Copy to clipboard ────────────────────────────────────────────────────
    function copyText(text) {
        function fallback() {
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.style.cssText = 'position:fixed;opacity:0;top:0;left:0;width:1px;height:1px;';
            document.body.appendChild(ta);
            ta.focus(); ta.select();
            try { document.execCommand('copy'); _showToast('Copied!'); }
            catch (e) { _showToast('Copy failed', true); }
            document.body.removeChild(ta);
        }
        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(text)
                .then(function() { _showToast('Copied!'); })
                .catch(fallback);
        } else {
            fallback();
        }
    }

    // ── Download helper ───────────────────────────────────────────────────────
    // Set by AndroidDownloadService.InitializeAsync() → routes downloads to .NET
    // which saves directly to the Android Downloads folder.
    // When null (desktop), standard blob-URL approach is used.
    var _dotNetDownloadHelper = null;

    function registerDownloadHelper(dotNetRef) {
        _dotNetDownloadHelper = dotNetRef;
        console.log('[MeshViewer] Android download helper registered.');
    }

    // Convert Uint8Array → base64 in safe chunks (avoids stack overflow on large files)
    function _uint8ToBase64(bytes) {
        var binary = '';
        var chunk = 8192;
        for (var i = 0; i < bytes.length; i += chunk) {
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
        }
        return btoa(binary);
    }

    // UTF-8 string → base64
    function _stringToBase64(str) {
        return btoa(unescape(encodeURIComponent(str)));
    }

    // Guess MIME type from filename extension
    function _guessMime(filename) {
        var ext = (filename.split('.').pop() || '').toLowerCase();
        var map = { zip:'application/zip', png:'image/png', jpg:'image/jpeg',
                    jpeg:'image/jpeg', obj:'model/obj', json:'application/json',
                    txt:'text/plain' };
        return map[ext] || 'application/octet-stream';
    }

    // ── downloadFile ──────────────────────────────────────────────────────────
    // content: string (text like .obj/.json) OR ArrayBuffer/Uint8Array
    function downloadFile(filename, content, mimeType) {
        if (_dotNetDownloadHelper) {
            var base64 = typeof content === 'string'
                ? _stringToBase64(content)
                : _uint8ToBase64(new Uint8Array(content));

            _dotNetDownloadHelper
                .invokeMethodAsync('SaveToDownloads', filename, base64, mimeType)
                .then(function(result) {
                    if (result && result.indexOf('OK') === 0)
                        _showToast('Saved: ' + filename);
                    else
                        _showToast(result || 'Download failed', true);
                })
                .catch(function(err) { _showToast('Error: ' + err, true); });
            return;
        }

        // Desktop: blob URL
        var blob = new Blob([content], { type: mimeType });
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url; a.download = filename;
        document.body.appendChild(a); a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
        _showToast('Downloaded: ' + filename);
    }

    // ── downloadArrayBuffer ───────────────────────────────────────────────────
    // arrayBuffer: number[] / Uint8Array / ArrayBuffer (zip, png, etc.)
    function downloadArrayBuffer(filename, arrayBuffer) {
        var mimeType = _guessMime(filename);

        if (_dotNetDownloadHelper) {
            var base64 = _uint8ToBase64(new Uint8Array(arrayBuffer));
            _dotNetDownloadHelper
                .invokeMethodAsync('SaveToDownloads', filename, base64, mimeType)
                .then(function(result) {
                    if (result && result.indexOf('OK') === 0)
                        _showToast('Saved: ' + filename);
                    else
                        _showToast(result || 'Download failed', true);
                })
                .catch(function(err) { _showToast('Error: ' + err, true); });
            return;
        }

        // Desktop: blob URL
        var blob = new Blob([new Uint8Array(arrayBuffer)], { type: mimeType });
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url; a.download = filename;
        document.body.appendChild(a); a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
        _showToast('Downloaded: ' + filename);
    }

    // Windows folder picker — returns the folder path via webkitdirectory input
    function pickFolder() {
        return new Promise(function(resolve) {
            var input = document.createElement("input");
            input.type = "file";
            input.setAttribute("webkitdirectory", "");
            input.style.display = "none";
            document.body.appendChild(input);
            input.onchange = function() {
                document.body.removeChild(input);
                if (!input.files || input.files.length === 0) { resolve(null); return; }
                var firstPath = input.files[0].path || "";
                if (firstPath) {
                    var sep = firstPath.indexOf("\\") >= 0 ? "\\" : "/";
                    var idx = Math.max(firstPath.lastIndexOf("\\"), firstPath.lastIndexOf("/"));
                    resolve(idx > 0 ? firstPath.substring(0, idx) : firstPath);
                } else { resolve(null); }
            };
            input.oncancel = function() { try { document.body.removeChild(input); } catch(e){} resolve(null); };
            input.click();
        });
    }

    return { init, registerDownloadHelper, downloadFile, downloadArrayBuffer, copyText, pickFolder };
})();
