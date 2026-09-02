
@import "C:\Users\soeur\.crossnote\style.less"

### About Maximum Parallel Image Loading
Texture loading requires first reading from disk into memory, then parsing/decoding it, committing it to video memory, and finally deleting it from memory. If a large number of images are read from disk into memory before they're committed to video memory, a significant amount of memory will be consumed (in the worst case, memory usage equals the total size of all texture images). This option limits the maximum number of texture images that can be retained in memory until they've been committed to video memory and the memory has been reclaimed, allowing further images to be loaded.

The default value is 5000, which is a reasonable value. If your CPU and memory are high-end, you can increase it accordingly.

Benchmark - 28211 textures on my PC:

| Maximum parallel loads | Time taken | Peak memory usage of images being loaded |
| :--- | :--- | :--- |
| 1000 | 17 seconds | 422 MB |
| 5000 | 5.7 seconds | 1.49 GB |
| 20000 | 6.3 seconds | 4.46 GB |