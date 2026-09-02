fn main() {
    csbindgen::Builder::default()
        .input_extern_file("src/lib.rs")
        .input_extern_file("src/types.rs")
        .input_extern_file("src/v2.rs")
        .csharp_dll_name("image_lib.dll")
        .csharp_namespace("ImageOpt")
        .csharp_class_name("NativeMethods")
        .csharp_use_function_pointer(false)
        .generate_csharp_file("../bindgen/NativeMethods.cs")
        .unwrap()
}
