use std::ffi::{CStr, CString, c_char};
use std::fs::File;
use std::io::Write;
use std::panic::catch_unwind;
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use crate::types::ToCString;
use hashbrown::HashMap;
use image_dds::ddsfile::{D3DFormat, Dds, DxgiFormat};
use mimalloc::MiMalloc;
use parking_lot::Mutex;

mod logger;
mod types;
mod v2;

#[global_allocator]
static GLOBAL: MiMalloc = MiMalloc;

#[unsafe(no_mangle)]
pub unsafe extern "C" fn compress_images(
    paths: *const *const c_char,
    len: i32,
    zstd: bool,
    overwrite: bool,
    gen_mipmap: bool,
    auto_scaling: bool,
    quality: i32,
    update: extern "C" fn(*const c_char, i64),
    process_start: extern "C" fn(total: i32),
    warn_log: extern "C" fn(*const c_char),
) {
    if paths.is_null() || len <= 0 {
        return;
    }

    /* let mut device = null_mut();
    unsafe {
        CoInitialize(std::ptr::null_mut());
        let hr = D3D11CreateDevice(
            null_mut(),
            D3D_DRIVER_TYPE_HARDWARE,
            null_mut(),
            0x00,
            null_mut(),
            0,
            D3D11_SDK_VERSION,
            &mut device,
            null_mut(),
            null_mut(),
        );
        if hr != 0 || device.is_null() {
            warn_log(
                "[ImageOpt] Failed to create D3D11 device."
                    .to_owned()
                    .to_cs(),
            );
            return;
        }
    }
    let device = SafePtr(NonNull::new(device).unwrap()); */

    let images = Arc::new(Mutex::new(Vec::new()));
    rayon::in_place_scope(|s| unsafe {
        let paths_slice: &[*const c_char] = std::slice::from_raw_parts(paths, len as usize);
        for ptr in paths_slice {
            let cstr = CStr::from_ptr(*ptr);
            let cow = cstr.to_string_lossy();
            let path = Path::new(&*cow).to_path_buf();

            let images = images.clone();
            s.spawn(move |_| {
                jwalk::WalkDir::new(path)
                    .into_iter()
                    .filter_map(|e| e.ok())
                    .filter(|e| e.file_type.is_file())
                    .filter(|e| {
                        e.path()
                            .extension()
                            .map(|ext| ext == "png" || ext == "jpg" || ext == "jpeg")
                            .unwrap_or(false)
                    })
                    .filter(|e| {
                        if overwrite {
                            return true;
                        }
                        if zstd {
                            return !e.path().with_extension("dds.zstd").exists();
                        }
                        return !e.path().with_extension("dds").exists();
                    })
                    .for_each(|e| {
                        images.lock().push(e.path());
                        update(e.path().display().to_string().to_cs(), 0);
                    });
            });
        }
    });
    process_start(images.lock().len() as i32);
    rayon::scope(|s| {
        let mut images = images.lock();
        for path in images.drain(..) {
            s.spawn(move |_| {
                let res = catch_unwind(|| {
                    compress_image_inner(&path, zstd, gen_mipmap, auto_scaling, quality)
                })
                .map_err(|e| anyhow::Error::msg(format!("{e:?}")))
                .flatten();
                match res {
                    Ok(size) => update(path.display().to_string().to_cs(), size),
                    Err(e) => warn_log(format!("[ImageOpt] {e}. {}", path.display()).to_cs()),
                }
            });
        }
    });
}

// GPU compression is slower...
/* fn compress_image_inner_gpu(
    path: &Path,
    mut device: SafePtr<ID3D11Device>,
    zstd: bool,
    gen_mipmap: bool,
    auto_scaling: bool,
) -> anyhow::Result<i64> {
    let mut img = image::open(&path)?;
    img.apply_orientation(image::metadata::Orientation::FlipVertical);
    if auto_scaling && (img.width() % 4 != 0 || img.height() % 4 != 0) {
        img = img.resize_exact(
            img.width() & !3,
            img.height() & !3,
            image::imageops::FilterType::Lanczos3,
        );
    }

    let width = img.width() as usize;
    let height = img.height() as usize;
    let mut pixels = img.into_rgba8();
    let pitch = directxtex::DXGI_FORMAT_R8G8B8A8_UNORM.compute_pitch(
        width,
        height,
        directxtex::CP_FLAGS_NONE,
    )?;
    let image = directxtex::Image {
        width,
        height,
        format: directxtex::DXGI_FORMAT_R8G8B8A8_UNORM,
        row_pitch: pitch.row,
        slice_pitch: pitch.slice,
        pixels: pixels.as_mut_ptr(),
    };
    let scratch_image = if gen_mipmap {
        image.generate_mip_maps(directxtex::TEX_FILTER_DEFAULT, 0, false)?
    } else {
        let mut img = ScratchImage::default();
        img.initialize_from_image(&image, false, directxtex::CP_FLAGS_NONE)?;
        img
    };
    let scratch_image = scratch_image.compress_gpu(
        unsafe { device.0.as_mut() },
        directxtex::DXGI_FORMAT_BC7_UNORM,
        directxtex::TEX_COMPRESS_DEFAULT,
        1.0,
    )?;
    let blob = scratch_image.save_dds(directxtex::DDS_FLAGS_NONE)?;

    let size;

    if zstd {
        let f = File::create(path.with_extension("dds.zstd"))?;
        let mut encoder = zstd::Encoder::new(f, 0)?;
        encoder.multithread(1)?;
        encoder.write_all(blob.buffer())?;
        let mut f = encoder.finish()?;
        f.flush()?;
        size = f.metadata().map(|f| f.len()).unwrap_or_default() as i64;
    } else {
        let mut f = File::create(path.with_extension("dds"))?;
        f.write_all(blob.buffer())?;
        f.flush()?;
        size = f.metadata().map(|f| f.len()).unwrap_or_default() as i64;
    }

    Ok(size)
} */

fn compress_image_inner(
    path: &Path,
    zstd: bool,
    gen_mipmap: bool,
    auto_scaling: bool,
    quality: i32,
) -> anyhow::Result<i64> {
    let mut img = image::open(&path)?.flipv();
    if auto_scaling && (img.width() % 4 != 0 || img.height() % 4 != 0) {
        img = img.resize_exact(
            img.width() & !3,
            img.height() & !3,
            image::imageops::FilterType::Lanczos3,
        );
    }

    let dds = image_dds::dds_from_image(
        &img.into_rgba8(),
        image_dds::ImageFormat::BC7RgbaUnormSrgb,
        match quality {
            0 => image_dds::Quality::Fast,
            1 => image_dds::Quality::Normal,
            _ => image_dds::Quality::Slow,
        },
        if gen_mipmap {
            image_dds::Mipmaps::GeneratedAutomatic
        } else {
            image_dds::Mipmaps::Disabled
        },
    )?;
    let size;

    if zstd {
        let f = File::create(path.with_extension("dds.zstd"))?;
        let mut encoder = zstd::Encoder::new(f, 0)?;
        encoder.multithread(1)?;
        dds.write(&mut encoder)?;
        let mut f = encoder.finish()?;
        f.flush()?;
        size = f.metadata().map(|f| f.len()).unwrap_or_default() as i64;
    } else {
        let mut f = File::create(path.with_extension("dds"))?;
        dds.write(&mut f)?;
        f.flush()?;
        size = f.metadata().map(|f| f.len()).unwrap_or_default() as i64;
    }

    Ok(size)
}

fn load_image_inner(path: &Path) -> anyhow::Result<ImageBuffer> {
    let mut img;
    // Some virtual files may not be directly accessible from the file system, such as "language.rar/xxx.png"
    if path.exists() {
        let file = File::open(path)?;
        let mmap = unsafe { memmap2::MmapOptions::new().populate().map(&file)? };
        img = image::load_from_memory(&mmap)?;
        img.apply_orientation(image::metadata::Orientation::FlipVertical);
    } else {
        anyhow::bail!("[ImageOpt] File not found: {}", path.display());
    }

    let width = img.width() as i32;
    let height = img.height() as i32;
    let mut buffer = img.to_rgba8().into_raw();

    let ptr = buffer.as_mut_ptr();
    let length = buffer.len() as i32;
    let capacity = buffer.capacity();
    std::mem::forget(buffer);
    Ok(ImageBuffer {
        ptr,
        length,
        capacity,
        width,
        height,
        is_dds: false,
        mipmap_count: 0,
        format: Default::default(),
    })
}

fn load_dds_inner(path: &Path, zstd: bool) -> anyhow::Result<Option<ImageBuffer>> {
    let file = File::open(path)?;
    let mmap = unsafe { memmap2::MmapOptions::new().populate().map(&file)? };

    let dds = if zstd {
        let decoder = zstd::Decoder::new(&*mmap)?;
        image_dds::ddsfile::Dds::read(decoder)?
    } else {
        image_dds::ddsfile::Dds::read(&*mmap)?
    };

    let width = dds.header.width as i32;
    let height = dds.header.height as i32;
    let mipmap_count = dds.get_num_mipmap_levels() as i32;

    if width % 4 != 0 || height % 4 != 0 {
        // Unity requires compressed format images to have dimensions that are multiples of 4
        return Ok(None);
    }

    let Some(format) = convert_to_unity_format(&dds) else {
        return Ok(None);
    };

    let mut buffer = dds.data;
    let ptr = buffer.as_mut_ptr();
    let length = buffer.len() as i32;
    let capacity = buffer.capacity();
    std::mem::forget(buffer);
    Ok(Some(ImageBuffer {
        ptr,
        length,
        capacity,
        width,
        height,
        mipmap_count,
        format,
        is_dds: true,
    }))
}

#[repr(C)]
#[derive(Default, Debug)]
pub struct ImageBuffer {
    pub ptr: *mut u8,
    pub length: i32,
    pub width: i32,
    pub height: i32,

    pub is_dds: bool,
    pub mipmap_count: i32,
    pub format: UnityTextureFormat,

    capacity: usize,
}

unsafe impl Send for ImageBuffer {}
unsafe impl Sync for ImageBuffer {}

static MAP: std::sync::LazyLock<Mutex<HashMap<i32, crossbeam::channel::Receiver<ImageBuffer>>>> =
    std::sync::LazyLock::new(|| Mutex::new(HashMap::new()));

static SEMAPHORE: once_cell::sync::OnceCell<(
    crossbeam::channel::Sender<()>,
    crossbeam::channel::Receiver<()>,
)> = once_cell::sync::OnceCell::new();

static POOL: std::sync::LazyLock<rayon::ThreadPool> = std::sync::LazyLock::new(|| {
    rayon::ThreadPoolBuilder::new()
        .num_threads(num_cpus::get())
        .build()
        .unwrap()
});

#[unsafe(no_mangle)]
pub unsafe extern "C" fn init_semaphore(limit: i32) {
    let _ = SEMAPHORE.get_or_init(|| create_semaphore(limit));
}

fn create_semaphore(
    limit: i32,
) -> (
    crossbeam::channel::Sender<()>,
    crossbeam::channel::Receiver<()>,
) {
    let (s, r) = crossbeam::channel::bounded(limit as usize);
    (s, r)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn load_image_async(
    ptr: *const u16,
    len: i32,
    enable_dds: bool,
    log: extern "C" fn(*const c_char),
    tid: i32,
) {
    let result = catch_unwind(|| {
        let slice = unsafe { std::slice::from_raw_parts(ptr, len as usize) };
        let str = String::from_utf16(slice).unwrap();
        let (s, r) = crossbeam::channel::bounded(1);
        POOL.spawn(move || {
            let path = Path::new(&str);
            let dds_path = path.with_extension("dds");
            let dds_zstd_path = path.with_extension("dds.zstd");

            if let Err(_) = SEMAPHORE.get_or_init(|| create_semaphore(10000)).0.send(()) {
                log(format!("ImageOpt: semaphore error").to_cs());
            }

            let mut buffer = ImageBuffer::default();
            let zstd = dds_zstd_path.exists();
            let mut dds = enable_dds && (dds_path.exists() || zstd);
            if dds {
                match load_dds_inner(
                    if zstd {
                        dds_zstd_path.as_path()
                    } else {
                        dds_path.as_path()
                    },
                    zstd,
                ) {
                    Ok(Some(b)) => buffer = b,
                    Ok(None) => {
                        dds = false;
                        log(format!("ImageOpt: failed to load {str}, because the format is not supported or the size is not a multiple of 4. using vanilla").to_cs());
                    }
                    Err(err) => {
                        log(format!("ImageOpt: load dds error: {:?}", err).to_cs());
                    }
                }
            }
            // None if the format is not supported, fallback to default format
            if !dds {
                buffer = match load_image_inner(path) {
                    Ok(buffer) => buffer,
                    Err(err) => {
                        log(format!("ImageOpt: load image error: {:?}", err).to_cs());
                        ImageBuffer::default()
                    }
                }
            }
            s.send(buffer).unwrap();
        });
        MAP.lock().insert(tid, r);
    });

    if let Err(err) = result {
        log(format!("ImageOpt: load image error: {:?}", err).to_cs());
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn get_image_buffer(
    tid: i32,
    warn_log: extern "C" fn(*const c_char),
) -> ImageBuffer {
    SEMAPHORE
        .get_or_init(|| create_semaphore(10000))
        .1
        .try_recv()
        .ok();
    let receiver = MAP.lock().remove(&tid);
    if let Some(receiver) = receiver {
        loop {
            match receiver.recv_timeout(Duration::from_secs(5)) {
                Ok(buffer) => return buffer,
                Err(err) => match err {
                    crossbeam::channel::RecvTimeoutError::Timeout => {
                        warn_log(format!("ImageOpt: load image timeout. repeating").to_cs());
                        continue;
                    }
                    crossbeam::channel::RecvTimeoutError::Disconnected => {
                        break;
                    }
                },
            }
        }
    }
    ImageBuffer::default()
}

#[deprecated]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn load_image(ptr: *const u16, len: i32, enable_dds: bool) -> ImageBuffer {
    if ptr.is_null() || len <= 0 {
        return ImageBuffer::default();
    }
    let slice = unsafe { std::slice::from_raw_parts(ptr, len as usize) };
    let str = String::from_utf16(slice).unwrap();
    let path = Path::new(&str);
    let dds_path = path.with_extension("dds");

    if enable_dds && dds_path.exists() {
        match load_dds_inner(dds_path.as_path(), false) {
            Ok(buffer) => buffer.unwrap(),
            Err(_) => ImageBuffer::default(),
        }
    } else {
        match load_image_inner(path) {
            Ok(buffer) => buffer,
            Err(_) => ImageBuffer::default(),
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn free_buffer(buffer: ImageBuffer) {
    if buffer.ptr.is_null() || buffer.length <= 0 {
        return;
    }

    let _ = unsafe { Vec::from_raw_parts(buffer.ptr, buffer.length as usize, buffer.capacity) };
}

/// from UnityEngine.TextureFormat
#[repr(u8)]
#[derive(Default, Debug, Clone, Copy)]
pub enum UnityTextureFormat {
    RGB24 = 3,
    #[default]
    RGBA32 = 4,
    ARGB32 = 5,
    RGB565 = 7,
    DXT1 = 10,
    DXT5 = 12,
    BGRA32 = 14,
    RHalf = 15,
    YUY2 = 21,
    BC6H = 24,
    BC7 = 25,
    BC4 = 26,
    BC5 = 27,
}

fn convert_to_unity_format(dds: &Dds) -> Option<UnityTextureFormat> {
    dds.get_dxgi_format()
        .map(|f| {
            Some(match f {
                DxgiFormat::BC1_UNorm | DxgiFormat::BC1_UNorm_sRGB => UnityTextureFormat::DXT1,
                DxgiFormat::BC4_SNorm | DxgiFormat::BC4_UNorm | DxgiFormat::BC4_Typeless => {
                    UnityTextureFormat::BC4
                }
                DxgiFormat::BC5_SNorm | DxgiFormat::BC5_UNorm | DxgiFormat::BC5_Typeless => {
                    UnityTextureFormat::BC5
                }
                DxgiFormat::BC6H_SF16 | DxgiFormat::BC6H_UF16 | DxgiFormat::BC6H_Typeless => {
                    UnityTextureFormat::BC6H
                }
                DxgiFormat::BC7_UNorm | DxgiFormat::BC7_UNorm_sRGB | DxgiFormat::BC7_Typeless => {
                    UnityTextureFormat::BC7
                }
                DxgiFormat::B8G8R8A8_UNorm => UnityTextureFormat::BGRA32,
                DxgiFormat::R8G8B8A8_UNorm => UnityTextureFormat::RGBA32,
                _ => return None,
            })
        })
        .or_else(|| {
            dds.get_d3d_format().map(|f| {
                Some(match f {
                    D3DFormat::DXT1 => UnityTextureFormat::DXT1,
                    D3DFormat::DXT5 => UnityTextureFormat::DXT5,
                    D3DFormat::R5G6B5 => UnityTextureFormat::RGB565,
                    D3DFormat::R8G8B8 => UnityTextureFormat::RGB24,
                    D3DFormat::A8R8G8B8 => UnityTextureFormat::ARGB32,
                    D3DFormat::YUY2 => UnityTextureFormat::YUY2,
                    D3DFormat::R16F => UnityTextureFormat::RHalf,
                    _ => return None,
                })
            })
        })
        .flatten()
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn free_string(str: *mut c_char) {
    unsafe {
        let _ = CString::from_raw(str);
    };
}

#[test]
fn test_t() {
    let path = r"E:\SteamLibrary\steamapps\workshop\content\294100\1677616980";
    jwalk::WalkDir::new(path)
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.file_type.is_file())
        .filter(|e| {
            e.path()
                .extension()
                .map(|ext| ext == "png" || ext == "jpg" || ext == "jpeg")
                .unwrap_or(false)
        })
        .for_each(|e| {});
}
