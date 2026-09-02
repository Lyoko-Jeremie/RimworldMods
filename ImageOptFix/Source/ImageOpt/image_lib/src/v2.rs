use crossbeam::atomic::AtomicCell;
use crossbeam::channel::{Receiver, Sender};
use image_dds::ddsfile::{Dds, DxgiFormat};
use once_cell::sync::{Lazy, OnceCell};
use parking_lot::Mutex;
use std::ffi::c_char;
use std::fs::File;
use std::panic::catch_unwind;
use std::path::{Path, PathBuf};
use std::ptr::NonNull;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};
use winapi::ctypes::c_void;
use winapi::shared::dxgiformat::DXGI_FORMAT;
use winapi::shared::dxgitype::DXGI_SAMPLE_DESC;
use winapi::um::d3d11::{
    D3D11_BIND_SHADER_RESOURCE, D3D11_SUBRESOURCE_DATA, D3D11_TEXTURE2D_DESC, ID3D11Device,
    ID3D11Texture2D,
};

use crate::logger::LOGGER_BUFFER;
use crate::types::ToCString;
use crate::{UnityTextureFormat, convert_to_unity_format, error, info, warn};

static THREAD_POOL: OnceCell<Mutex<Vec<JoinHandle<()>>>> = OnceCell::new();
pub static D3D11_DEVICE: OnceCell<Mutex<SafePtr<ID3D11Device>>> = OnceCell::new();

static LOAD_QUEUE: Lazy<(
    parking_lot::RwLock<Sender<(PathBuf, i32)>>,
    Receiver<(PathBuf, i32)>,
)> = Lazy::new(|| {
    let (tx, rx) = crossbeam::channel::unbounded();
    (parking_lot::RwLock::new(tx), rx)
});
static LOADED: Lazy<papaya::HashMap<i32, ImageData, hashbrown::DefaultHashBuilder>> =
    Lazy::new(papaya::HashMap::default);

static EXPIRED: AtomicCell<i32> = AtomicCell::new(0);
static AUTO_COMPRESS: AtomicCell<bool> = AtomicCell::new(false);

pub struct SafePtr<T>(pub NonNull<T>);

unsafe impl<T> Send for SafePtr<T> {}
unsafe impl<T> Sync for SafePtr<T> {}
impl<T> Clone for SafePtr<T> {
    fn clone(&self) -> Self {
        Self(self.0)
    }
}
impl<T> Copy for SafePtr<T> {}

#[repr(C)]
#[derive(Default, Clone, Copy)]
pub struct ImageData {
    width: i32,
    height: i32,
    format: UnityTextureFormat,
    mip_count: i32,
    is_dds: bool,
    error: i32,
    ptr: *mut u8,
}

unsafe impl Send for ImageData {}
unsafe impl Sync for ImageData {}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn get_expired() -> i32 {
    EXPIRED.load()
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn set_auto_compress(value: bool) {
    AUTO_COMPRESS.store(value);
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn get_device(ptr: *const u8) -> bool {
    let ptr = ptr as *mut ID3D11Texture2D;
    if ptr.is_null() {
        return false;
    }
    let mut device = std::ptr::null_mut();
    unsafe {
        (*ptr).GetDevice(&mut device);
        if device.is_null() {
            return false;
        }
        (*device).AddRef();
        D3D11_DEVICE
            .set(Mutex::new(SafePtr(NonNull::new_unchecked(device))))
            .is_ok()
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn enqueue_image(ptr: *const u16, len: i32, id: i32) {
    THREAD_POOL.get_or_init(create_thread_pool);
    let slice = unsafe { std::slice::from_raw_parts(ptr, len as usize) };
    let str = String::from_utf16(slice).unwrap();
    let path = PathBuf::from(str);
    // LOAD_QUEUE.0.read().send((path, id)).ok();
    if let Err(error) = LOAD_QUEUE.0.read().send((path, id)) {
        error!("Failed to enqueue image id {id}: {error}");
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn flush_log(
    log: extern "C" fn(*const c_char),
    warn_log: extern "C" fn(*const c_char),
    err_log: extern "C" fn(*const c_char),
) {
    while let Some((level, message)) = LOGGER_BUFFER.pop() {
        match level {
            log::Level::Error => err_log(message.to_cs()),
            log::Level::Warn => warn_log(message.to_cs()),
            log::Level::Info => log(message.to_cs()),
            _ => {}
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn wait_load_images() {
    let (tx, _) = crossbeam::channel::bounded(0);
    drop(std::mem::replace(&mut *LOAD_QUEUE.0.write(), tx));
    let mut handles = THREAD_POOL.get_or_init(create_thread_pool).lock();
    for handle in handles.drain(..) {
        handle.join().unwrap();
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn get_texture(id: i32) -> ImageData {
    if let Some(data) = LOADED.pin().remove(&id) {
        *data
    } else {
        warn!("Image with id {} not found", id);
        ImageData {
            error: 1,
            ptr: std::ptr::null_mut(),
            ..Default::default()
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn load_image_sync(ptr: *const u16, len: i32) -> ImageData {
    let slice = unsafe { std::slice::from_raw_parts(ptr, len as usize) };
    let str = String::from_utf16(slice).unwrap();
    let path = PathBuf::from(str);
    match create_texture_from_file(&path) {
        Ok(data) => data,
        Err(e) => {
            error!("Failed to load image {:?}: {e:?}", path.display());
            ImageData {
                error: 1,
                ptr: std::ptr::null_mut(),
                ..Default::default()
            }
        }
    }
}

fn create_texture(
    width: u32,
    height: u32,
    mipmap_count: u32,
    block_size: u32,
    format: DxgiFormat,
    data: &[u8],
) -> anyhow::Result<SafePtr<ID3D11Texture2D>> {
    let mut device = D3D11_DEVICE.get().unwrap().lock();
    let mut desc = D3D11_TEXTURE2D_DESC {
        Width: width,
        Height: height,
        MipLevels: mipmap_count,
        ArraySize: 1,
        Format: erase_srgb(format) as DXGI_FORMAT,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Usage: winapi::um::d3d11::D3D11_USAGE_IMMUTABLE,
        BindFlags: D3D11_BIND_SHADER_RESOURCE,
        CPUAccessFlags: 0,
        MiscFlags: 0,
    };
    let mut texture = std::ptr::null_mut();
    let mut initial_data = Vec::new();

    let mut cursor = 0;
    for mip_level in 0..mipmap_count as u32 {
        let mip_width = 1.max(width >> mip_level);
        let mip_height = 1.max(height >> mip_level);
        let mip_size_bytes = 1.max((mip_width + 3) / 4) as usize
            * 1.max((mip_height + 3) / 4) as usize
            * block_size as usize;
        let buf = &data[cursor..cursor + mip_size_bytes];
        let mut src_row_pitch = 1.max((mip_width + 3) / 4) as u32 * block_size as u32;
        if format == DxgiFormat::R8G8B8A8_UNorm {
            src_row_pitch = width * 4;
        }

        initial_data.push(D3D11_SUBRESOURCE_DATA {
            pSysMem: buf.as_ptr() as *const c_void,
            SysMemPitch: src_row_pitch,
            SysMemSlicePitch: 0,
        });

        cursor += mip_size_bytes;
    }

    unsafe {
        let err = device
            .0
            .as_mut()
            .CreateTexture2D(&mut desc, initial_data.as_ptr(), &mut texture);
        if winapi::shared::winerror::FAILED(err) || texture.is_null() {
            return Err(anyhow::anyhow!("Failed to create texture. code: {err}"));
        }
        Ok(SafePtr(NonNull::new_unchecked(
            texture as *mut ID3D11Texture2D,
        )))
    }
}

fn create_thread_pool() -> Mutex<Vec<JoinHandle<()>>> {
    Mutex::new(
        (0..num_cpus::get())
            .map(|_| {
                std::thread::spawn(|| {
                    while let Ok((path, id)) = LOAD_QUEUE.1.recv() {
                        let result = catch_unwind::<_, anyhow::Result<()>>(|| {
                            let data = create_texture_from_file(&path)?;
                            LOADED.pin().insert(id, data);
                            Ok(())
                        })
                        .map_err(|e| anyhow::anyhow!("{e:?}"));
                        if let Err(e) = result.flatten() {
                            warn!("load image {:?}: {e:?}", path.display());
                        };
                    }
                })
            })
            .collect(),
    )
}

#[inline]
fn create_texture_from_file(path: &Path) -> anyhow::Result<ImageData> {
    read_image(&path, |mut data, buffer, format, block_size| {
        let texture = create_texture(
            data.width as u32,
            data.height as u32,
            data.mip_count as u32,
            block_size,
            format,
            buffer,
        )?;
        data.ptr = texture.0.as_ptr() as *mut u8;
        Ok(data)
    })
    .flatten()
}

fn read_image<R>(
    original_path: &Path,
    reader: impl FnOnce(ImageData, &[u8], DxgiFormat, u32) -> R,
) -> anyhow::Result<R> {
    let dds_zstd_path = original_path.with_extension("dds.zstd");
    let dds_path = original_path.with_extension("dds");
    let mut is_zstd = false;
    let mut is_dds = false;
    let mut only_dds = false;

    let path = if dds_zstd_path.exists() {
        is_zstd = true;
        is_dds = true;
        dds_zstd_path
    } else if dds_path.exists() {
        is_dds = true;
        dds_path
    } else {
        original_path.to_path_buf()
    };

    let mut original_path = original_path.to_path_buf();
    if original_path
        .extension()
        .map(|e| e.to_ascii_lowercase() == "dds")
        .unwrap_or_default()
    {
        if original_path.with_extension("png").exists() {
            original_path = original_path.with_extension("png");
        } else if original_path.with_extension("jpg").exists() {
            original_path = original_path.with_extension("jpg");
        } else {
            only_dds = true;
        }
    }

    if let (Ok(origin), Ok(current)) = (
        original_path
            .metadata()
            .map(|meta| meta.modified())
            .flatten(),
        path.metadata().map(|meta| meta.modified()).flatten(),
    ) && !only_dds
    {
        if origin > current {
            if AUTO_COMPRESS.load() {
                let time = Instant::now();

                if let Err(e) = crate::compress_image_inner(&original_path, is_zstd, true, true, 2) {
                    warn!(
                        "image {} has expired, but recompress failed: {:?}",
                        original_path.display(),
                        e
                    );
                    EXPIRED.fetch_add(1);
                } else {
                    info!(
                        "image {} has expired, recompressed. time: {:?}",
                        original_path.display(),
                        time.elapsed()
                    );
                }
            } else {
                EXPIRED.fetch_add(1);
            }
        };
    }

    let f = File::open(&path)?;
    let mmap = unsafe { memmap2::MmapOptions::new().populate().map(&f)? };
    Ok(if is_dds {
        let dds = if is_zstd {
            Dds::read(zstd::Decoder::new(&*mmap)?)?
        } else {
            Dds::read(&*mmap)?
        };

        let block_size = dds
            .get_format()
            .map(|f| f.get_block_size())
            .flatten()
            .ok_or_else(|| anyhow::anyhow!("Unsupported DDS format"))?;
        let Some(format) = dds.get_dxgi_format() else {
            anyhow::bail!("Unsupported DXGI format");
        };

        reader(
            ImageData {
                width: dds.get_width() as i32,
                height: dds.get_height() as i32,
                format: convert_to_unity_format(&dds)
                    .ok_or_else(|| anyhow::anyhow!("Unsupported DDS format"))?,
                is_dds: true,
                mip_count: dds.get_num_mipmap_levels() as i32,
                ..Default::default()
            },
            &dds.data,
            format,
            block_size,
        )
    } else {
        let mut img = image::load_from_memory(&*mmap)?;
        img.apply_orientation(image::metadata::Orientation::FlipVertical);
        reader(
            ImageData {
                width: img.width() as i32,
                height: img.height() as i32,
                format: UnityTextureFormat::RGBA32,
                is_dds: false,
                mip_count: 1,
                ..Default::default()
            },
            &img.into_rgba8(),
            DxgiFormat::R8G8B8A8_UNorm,
            4,
        )
    })
}

fn erase_srgb(format: DxgiFormat) -> DxgiFormat {
    match format {
        DxgiFormat::BC1_UNorm_sRGB => DxgiFormat::BC1_UNorm,
        DxgiFormat::BC2_UNorm_sRGB => DxgiFormat::BC2_UNorm,
        DxgiFormat::BC3_UNorm_sRGB => DxgiFormat::BC3_UNorm,
        DxgiFormat::BC4_SNorm => DxgiFormat::BC4_UNorm,
        DxgiFormat::BC5_SNorm => DxgiFormat::BC5_UNorm,
        DxgiFormat::BC6H_SF16 => DxgiFormat::BC6H_UF16,
        DxgiFormat::BC7_UNorm_sRGB => DxgiFormat::BC7_UNorm,
        _ => format,
    }
}
