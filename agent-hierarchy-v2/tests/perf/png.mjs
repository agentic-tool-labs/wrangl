// Decode the PNGs Chrome's Page.captureScreenshot returns (8-bit RGB or RGBA, not interlaced) into RGBA bytes.
import { inflateSync } from "node:zlib";

export function decodePng(buf) {
  if (buf.readUInt32BE(0) !== 0x89504e47) throw new Error("not a PNG");
  let at = 8;
  let width = 0, height = 0, channels = 0;
  const idat = [];
  while (at < buf.length) {
    const len = buf.readUInt32BE(at);
    const type = buf.toString("latin1", at + 4, at + 8);
    const data = buf.subarray(at + 8, at + 8 + len);
    if (type === "IHDR") {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      const [depth, colour, , , interlace] = data.subarray(8);
      channels = { 2: 3, 6: 4 }[colour];
      if (depth !== 8 || !channels || interlace) throw new Error(`unsupported PNG: depth ${depth}, colour type ${colour}, interlace ${interlace}`);
    } else if (type === "IDAT") {
      idat.push(data);
    } else if (type === "IEND") {
      break;
    }
    at += 12 + len;
  }
  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  const out = Buffer.alloc(width * height * 4);
  let prev = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let i = 0; i < stride; i++) {
      const a = i >= channels ? line[i - channels] : 0;
      const b = prev[i];
      const c = i >= channels ? prev[i - channels] : 0;
      let p;
      if (filter === 0) p = 0;
      else if (filter === 1) p = a;
      else if (filter === 2) p = b;
      else if (filter === 3) p = (a + b) >> 1;
      else {
        const e = a + b - c;
        const [da, db, dc] = [Math.abs(e - a), Math.abs(e - b), Math.abs(e - c)];
        p = da <= db && da <= dc ? a : db <= dc ? b : c;
      }
      line[i] = (line[i] + p) & 255;
    }
    for (let x = 0; x < width; x++) {
      const o = (y * width + x) * 4;
      out[o] = line[x * channels];
      out[o + 1] = line[x * channels + 1];
      out[o + 2] = line[x * channels + 2];
      out[o + 3] = channels === 4 ? line[x * channels + 3] : 255;
    }
    prev = line;
  }
  return { width, height, data: out };
}

// The [r, g, b] of every pixel inside a box (CSS px times the scale), clipped to the image.
export function pixelsIn(img, [l, t, r, b], scale = 1) {
  const px = [];
  const [x0, y0] = [Math.max(0, Math.floor(l * scale)), Math.max(0, Math.floor(t * scale))];
  const [x1, y1] = [Math.min(img.width, Math.ceil(r * scale)), Math.min(img.height, Math.ceil(b * scale))];
  for (let y = y0; y < y1; y++) {
    for (let x = x0; x < x1; x++) {
      const o = (y * img.width + x) * 4;
      px.push([img.data[o], img.data[o + 1], img.data[o + 2]]);
    }
  }
  return px;
}
