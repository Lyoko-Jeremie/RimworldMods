use crossbeam::queue::SegQueue;

pub static LOGGER_BUFFER: SegQueue<(log::Level, String)> = SegQueue::new();

#[macro_export]
macro_rules! info {
    ($($arg:tt)*) => {
        LOGGER_BUFFER.push((log::Level::Info, format!($($arg)*)));
    };
}

#[macro_export]
macro_rules! warn {
    ($($arg:tt)*) => {
        LOGGER_BUFFER.push((log::Level::Warn, format!($($arg)*)));
    };
}


#[macro_export]
macro_rules! error {
    ($($arg:tt)*) => {
        LOGGER_BUFFER.push((log::Level::Error, format!($($arg)*)));
    };
}
