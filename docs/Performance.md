# Measured performance

Through a real mount, on a real driver — **generated** by
`PerformanceMatrixEndToEndTests`. Do not edit by hand.

Tiers are separated by CONFIGURATION, not by hardware: `RAM cache` is a pool whose cache
dwarfs the working set read twice, `Storage` is a pool whose cache is far smaller read after a
remount, and `Landing` is a tiered pool. The landing-zone rows therefore price the CODE PATH,
not an SSD-versus-HDD difference — both members share one device on a test machine, so only a
host with genuinely different devices can price the tiering itself.

Host: 20 logical CPUs; multi-thread rows use 20 threads.

| Tier | Workload | Threads | Result |
| --- | --- | --- | ---: |
| RAM cache | sequential write, 1.5 GiB | 1 | 683 MiB/s |
| RAM cache | sequential read, 1.5 GiB | 1 | 3,167 MiB/s |
| Storage | sequential write, 1.5 GiB | 1 | 712 MiB/s |
| Storage | sequential read, 1.5 GiB | 1 | 2,153 MiB/s |
| Landing | sequential write, 1.5 GiB | 1 | 703 MiB/s |
| RAM cache | random 4 KiB read | 1 | 73,011 IOPS |
| RAM cache | random 4 KiB read | 20 | 56,626 IOPS |
| Storage | random 4 KiB read | 1 | 3,682 IOPS |
| Storage | random 4 KiB read | 20 | 3,158 IOPS |
| RAM cache | random 4 KiB read scaling | 1 -> 20 | 78 % of single-thread |
| Storage | create+write+close, 3072 B | 1 | 67 IOPS |
| Storage | create+write+close, 3072 B | 20 | 103 IOPS |
| Landing | create+write+close, 3072 B | 20 | 96 IOPS |
| RAM cache | open+read+close, 3072 B | 1 | 2,259 IOPS |
| RAM cache | open+read+close, 3072 B | 20 | 10,906 IOPS |
| RAM cache | open+read+close scaling, 3072 B | 1 -> 20 | 4.8x |
