fn main() {
    slint_build::compile("ui/app.slint").expect("desktop UI should compile");

    #[cfg(windows)]
    {
        let mut resource = winresource::WindowsResource::new();
        resource.set_icon("../../src/PocketAgent.Desktop/Assets/PocketAgent.ico");
        resource
            .compile()
            .expect("PocketAgent Windows icon resource should compile");
    }
}
