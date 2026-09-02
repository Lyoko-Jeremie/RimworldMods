use std::ffi::c_char;

pub trait ToCString {
    fn to_cs(self) -> *const c_char;
}

impl ToCString for String {
    fn to_cs(self) -> *const c_char {
        std::ffi::CString::new(self)
            .unwrap_or_else(|_| std::ffi::CString::new("faild to cstring").unwrap())
            .into_raw()
    }
}
