#[cfg(not(windows))]
fn main() {
    std::process::exit(2);
}

#[cfg(windows)]
mod windows_proxy {
    use std::{
        ffi::c_void,
        io::{self, Write},
        mem::size_of,
        os::windows::ffi::OsStrExt,
        path::Path,
    };

    use windows::{
        Win32::{
            Foundation::SIZE,
            Graphics::Gdi::{
                BI_RGB, BITMAP, BITMAPINFO, DIB_RGB_COLORS, DeleteObject, GetDC, GetDIBits,
                GetObjectW, HBITMAP, HGDIOBJ, ReleaseDC,
            },
            System::Com::{COINIT_APARTMENTTHREADED, CoInitializeEx, CoUninitialize},
            UI::Shell::{
                IShellItemImageFactory, SHCreateItemFromParsingName, SIIGBF_BIGGERSIZEOK,
                SIIGBF_ICONONLY, SIIGBF_SCALEUP, SIIGBF_THUMBNAILONLY,
            },
        },
        core::PCWSTR,
    };

    const MIN_THUMBNAIL_SIZE: i32 = 24;
    const MAX_THUMBNAIL_SIZE: i32 = 512;
    const BITMAP_FILE_HEADER_SIZE: usize = 14;
    const BITMAP_V5_HEADER_SIZE: usize = 124;
    const BITMAP_PIXEL_OFFSET: usize = BITMAP_FILE_HEADER_SIZE + BITMAP_V5_HEADER_SIZE;
    const BI_BITFIELDS: u32 = 3;
    const LCS_SRGB: u32 = 0x7352_4742;
    const LCS_GM_IMAGES: u32 = 4;

    #[derive(Clone, Copy)]
    enum ExtractionMode {
        Thumbnail,
        Icon,
    }

    struct ComGuard;

    impl Drop for ComGuard {
        fn drop(&mut self) {
            // SAFETY: The guard is created only after a successful CoInitializeEx.
            unsafe { CoUninitialize() };
        }
    }

    struct BitmapGuard(HBITMAP);

    impl Drop for BitmapGuard {
        fn drop(&mut self) {
            // SAFETY: IShellItemImageFactory transfers ownership of the HBITMAP.
            unsafe {
                let _ = DeleteObject(HGDIOBJ(self.0.0));
            }
        }
    }

    pub fn run() -> Result<(), String> {
        let mut arguments = std::env::args_os();
        let _executable = arguments.next();
        let mut first = arguments
            .next()
            .ok_or_else(|| "missing path argument".to_string())?;
        if first == "--self-test" {
            let pixels = vec![
                0x00, 0x00, 0xFF, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
                0xFF, 0xFF,
            ];
            return write_stdout(&encode_bgra_as_bitmap_v5(2, 2, pixels)?);
        }

        let mode = if first == "--icon-only" {
            first = arguments
                .next()
                .ok_or_else(|| "missing icon path argument".to_string())?;
            ExtractionMode::Icon
        } else {
            ExtractionMode::Thumbnail
        };

        let size = arguments
            .next()
            .and_then(|value| value.to_string_lossy().parse::<i32>().ok())
            .unwrap_or(256)
            .clamp(MIN_THUMBNAIL_SIZE, MAX_THUMBNAIL_SIZE);
        if arguments.next().is_some() {
            return Err("unexpected extra arguments".to_string());
        }

        let path = Path::new(&first);
        if !path.is_file() {
            return Err("Shell image source is not a file".to_string());
        }

        // SAFETY: COM is balanced by ComGuard and all Shell interfaces stay on
        // this single STA thread for the lifetime of the request.
        unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) }
            .ok()
            .map_err(|error| format!("COM initialization failed: {error}"))?;
        let _com_guard = ComGuard;

        let parsing_name: Vec<u16> = path
            .as_os_str()
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        // SAFETY: The parsing name remains alive for the call and the generic
        // return type supplies the exact requested COM interface IID.
        let factory: IShellItemImageFactory =
            unsafe { SHCreateItemFromParsingName(PCWSTR(parsing_name.as_ptr()), None) }
                .map_err(|error| format!("Shell item creation failed: {error}"))?;
        let flags = match mode {
            // THUMBNAILONLY is important: returning an icon here would make a
            // missing third-party thumbnail indistinguishable from a real preview.
            ExtractionMode::Thumbnail => {
                SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_SCALEUP
            }
            // ICONONLY asks the Shell item itself to resolve PIDL/AppUserModelID
            // shortcuts. ADDOVERLAYS is deliberately omitted so DeskBox's
            // "hide shortcut arrows" setting remains effective.
            ExtractionMode::Icon => SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_SCALEUP,
        };
        // SAFETY: The returned bitmap is owned by the caller and released by
        // BitmapGuard after its pixels have been copied.
        let bitmap = unsafe { factory.GetImage(SIZE { cx: size, cy: size }, flags) }
            .map_err(|error| format!("Shell image extraction failed: {error}"))?;
        let bitmap_guard = BitmapGuard(bitmap);
        let bytes = bitmap_to_bmp_bytes(bitmap_guard.0)?;
        write_stdout(&bytes)
    }

    fn bitmap_to_bmp_bytes(bitmap_handle: HBITMAP) -> Result<Vec<u8>, String> {
        let mut bitmap = BITMAP::default();
        // SAFETY: bitmap points to writable BITMAP storage and the handle is
        // valid for the duration of this function.
        let object_size = unsafe {
            GetObjectW(
                HGDIOBJ(bitmap_handle.0),
                size_of::<BITMAP>() as i32,
                Some((&mut bitmap as *mut BITMAP).cast::<c_void>()),
            )
        };
        if object_size != size_of::<BITMAP>() as i32 || bitmap.bmWidth <= 0 || bitmap.bmHeight == 0
        {
            return Err("Shell returned an invalid image bitmap".to_string());
        }

        let width = bitmap.bmWidth;
        let height = bitmap.bmHeight.abs();
        let pixel_byte_count = (width as usize)
            .checked_mul(height as usize)
            .and_then(|value| value.checked_mul(4))
            .ok_or_else(|| "thumbnail dimensions overflowed".to_string())?;
        let mut pixels = vec![0u8; pixel_byte_count];
        let mut bitmap_info = BITMAPINFO::default();
        bitmap_info.bmiHeader.biSize =
            size_of::<windows::Win32::Graphics::Gdi::BITMAPINFOHEADER>() as u32;
        bitmap_info.bmiHeader.biWidth = width;
        bitmap_info.bmiHeader.biHeight = -height;
        bitmap_info.bmiHeader.biPlanes = 1;
        bitmap_info.bmiHeader.biBitCount = 32;
        bitmap_info.bmiHeader.biCompression = BI_RGB.0;
        bitmap_info.bmiHeader.biSizeImage = pixel_byte_count as u32;

        // SAFETY: GetDC/ReleaseDC are balanced. pixels and bitmap_info remain
        // valid and correctly sized for the requested top-down 32-bit DIB.
        let device_context = unsafe { GetDC(None) };
        if device_context.0.is_null() {
            return Err("unable to acquire a screen device context".to_string());
        }
        let copied_rows = unsafe {
            GetDIBits(
                device_context,
                bitmap_handle,
                0,
                height as u32,
                Some(pixels.as_mut_ptr().cast::<c_void>()),
                &mut bitmap_info,
                DIB_RGB_COLORS,
            )
        };
        unsafe {
            let _ = ReleaseDC(None, device_context);
        }
        if copied_rows != height {
            return Err("unable to copy Shell image pixels".to_string());
        }

        encode_bgra_as_bitmap_v5(width, height, pixels)
    }

    fn encode_bgra_as_bitmap_v5(
        width: i32,
        height: i32,
        mut pixels: Vec<u8>,
    ) -> Result<Vec<u8>, String> {
        if width <= 0 || height <= 0 || pixels.len() != width as usize * height as usize * 4 {
            return Err("invalid BGRA thumbnail payload".to_string());
        }

        // Several legacy Shell handlers return an opaque DDB with every alpha
        // byte cleared. Preserve that compatibility only when color data is
        // actually present; an all-zero bitmap is a blank result and must not
        // be promoted to an opaque black image or cached by DeskBox.
        if pixels.chunks_exact(4).all(|pixel| pixel[3] == 0) {
            if !pixels
                .chunks_exact(4)
                .any(|pixel| pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0)
            {
                return Err("Shell returned an empty transparent bitmap".to_string());
            }

            for pixel in pixels.chunks_exact_mut(4) {
                pixel[3] = 0xFF;
            }
        }

        let total_size = BITMAP_PIXEL_OFFSET
            .checked_add(pixels.len())
            .ok_or_else(|| "thumbnail payload is too large".to_string())?;
        let mut output = Vec::with_capacity(total_size);
        output.extend_from_slice(b"BM");
        write_u32(&mut output, total_size as u32);
        write_u16(&mut output, 0);
        write_u16(&mut output, 0);
        write_u32(&mut output, BITMAP_PIXEL_OFFSET as u32);

        write_u32(&mut output, BITMAP_V5_HEADER_SIZE as u32);
        write_i32(&mut output, width);
        write_i32(&mut output, -height);
        write_u16(&mut output, 1);
        write_u16(&mut output, 32);
        write_u32(&mut output, BI_BITFIELDS);
        write_u32(&mut output, pixels.len() as u32);
        write_i32(&mut output, 0);
        write_i32(&mut output, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0x00FF_0000);
        write_u32(&mut output, 0x0000_FF00);
        write_u32(&mut output, 0x0000_00FF);
        write_u32(&mut output, 0xFF00_0000);
        write_u32(&mut output, LCS_SRGB);
        output.resize(output.len() + 36, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, LCS_GM_IMAGES);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0);
        debug_assert_eq!(output.len(), BITMAP_PIXEL_OFFSET);
        output.extend_from_slice(&pixels);
        Ok(output)
    }

    fn write_stdout(bytes: &[u8]) -> Result<(), String> {
        let stdout = io::stdout();
        let mut handle = stdout.lock();
        handle
            .write_all(bytes)
            .and_then(|_| handle.flush())
            .map_err(|error| format!("unable to write thumbnail payload: {error}"))
    }

    fn write_u16(output: &mut Vec<u8>, value: u16) {
        output.extend_from_slice(&value.to_le_bytes());
    }

    fn write_u32(output: &mut Vec<u8>, value: u32) {
        output.extend_from_slice(&value.to_le_bytes());
    }

    fn write_i32(output: &mut Vec<u8>, value: i32) {
        output.extend_from_slice(&value.to_le_bytes());
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn bitmap_v5_payload_has_alpha_header_and_opaque_legacy_fallback() {
            let payload = encode_bgra_as_bitmap_v5(1, 1, vec![0x11, 0x22, 0x33, 0x00])
                .expect("bitmap payload");

            assert_eq!(&payload[0..2], b"BM");
            assert_eq!(
                u32::from_le_bytes(payload[10..14].try_into().unwrap()) as usize,
                BITMAP_PIXEL_OFFSET,
            );
            assert_eq!(
                u32::from_le_bytes(payload[54..58].try_into().unwrap()),
                0x00FF_0000,
            );
            assert_eq!(payload[BITMAP_PIXEL_OFFSET + 3], 0xFF);
        }

        #[test]
        fn bitmap_v5_payload_rejects_empty_transparent_result() {
            let error = encode_bgra_as_bitmap_v5(1, 1, vec![0x00, 0x00, 0x00, 0x00])
                .expect_err("empty transparent bitmap must be rejected");

            assert!(error.contains("empty transparent bitmap"));
        }
    }
}

#[cfg(windows)]
fn main() {
    if let Err(error) = windows_proxy::run() {
        eprintln!("{error}");
        std::process::exit(4);
    }
}
