# Measured performance

Through a real mount, on a real driver — **generated** by
`PerformanceMatrixEndToEndTests`. Do not edit by hand.

Tiers are separated by CONFIGURATION, not by hardware: `RAM cache` is a pool whose cache
dwarfs the working set read twice, `Storage` is a pool whose cache is far smaller read after a
remount, and `Landing` is a tiered pool. The landing-zone rows therefore price the CODE PATH,
not an SSD-versus-HDD difference — both members share one device on a test machine, so only a
host with genuinely different devices can price the tiering itself.

The `Scatter` rows price OVERLAPPED I/O directly, and they need no second device to mean
something: the control is the same pool with `io.queueDepthPerVolume` pinned to 1, which is
one outstanding request at a time — what the engine used to do everywhere. The `2 copies` row
does share one device here, so it prices the split's overhead rather than the gain.

Host: 20 logical CPUs; multi-thread rows use 20 threads.

| Tier | Workload | Threads | Result |
| --- | --- | --- | ---: |
| RAM cache | sequential write, 1.5 GiB | 1 | 1,016 MiB/s |
| RAM cache | sequential read, 1.5 GiB | 1 | 2,471 MiB/s |
| Storage | sequential write, 1.5 GiB | 1 | 1,002 MiB/s |
| Storage | sequential read, 1.5 GiB | 1 | 1,634 MiB/s |
| Landing | sequential write, 1.5 GiB | 1 | 1,060 MiB/s |
| RAM ack | sequential write, 1.5 GiB (opt-in) | 1 | 1,048 MiB/s |
| RAM ack | vs. durability-first default | 1 | 1.03x |
| RAM cache | random 4 KiB read | 1 | 73,782 IOPS |
| RAM cache | random 4 KiB read | 20 | 51,757 IOPS |
| Storage | random 4 KiB read | 1 | 3,238 IOPS |
| Storage | random 4 KiB read | 20 | 3,445 IOPS |
| RAM cache | random 4 KiB read scaling | 1 -> 20 | 70 % of single-thread |
| Storage | create+write+close, 3072 B | 1 | 119 IOPS |
| Storage | create+write+close, 3072 B | 20 | 144 IOPS |
| Landing | create+write+close, 3072 B | 20 | 157 IOPS |
| RAM cache | open+read+close, 3072 B | 1 | 2,273 IOPS |
| RAM cache | open+read+close, 3072 B | 20 | 3,042 IOPS |
| RAM cache | open+read+close scaling, 3072 B | 1 -> 20 | 1.3x |
| Scatter | sequential read, 512 MiB, queue depth 1 | 1 | 1,788 MiB/s |
| Scatter | sequential read, 512 MiB, overlapped | 1 | 1,584 MiB/s |
| Scatter | overlapped vs. queue depth 1 | 1 | 0.89x |
| Scatter | sequential read, 512 MiB, 2 copies | 1 | 1,878 MiB/s |
