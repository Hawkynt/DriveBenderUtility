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
| RAM cache | sequential write, 1.5 GiB | 1 | 679 MiB/s |
| RAM cache | sequential read, 1.5 GiB | 1 | 1,962 MiB/s |
| Storage | sequential write, 1.5 GiB | 1 | 562 MiB/s |
| Storage | sequential read, 1.5 GiB | 1 | 1,890 MiB/s |
| Landing | sequential write, 1.5 GiB | 1 | 653 MiB/s |
| RAM ack | sequential write, 1.5 GiB (opt-in) | 1 | 956 MiB/s |
| RAM ack | vs. durability-first default | 1 | 1.41x |
| RAM cache | random 4 KiB read | 1 | 55,812 IOPS |
| RAM cache | random 4 KiB read | 20 | 42,557 IOPS |
| Storage | random 4 KiB read | 1 | 2,738 IOPS |
| Storage | random 4 KiB read | 20 | 2,810 IOPS |
| RAM cache | random 4 KiB read scaling | 1 -> 20 | 76 % of single-thread |
| Storage | create+write+close, 3072 B | 1 | 67 IOPS |
| Storage | create+write+close, 3072 B | 20 | 98 IOPS |
| Landing | create+write+close, 3072 B | 20 | 96 IOPS |
| RAM cache | open+read+close, 3072 B | 1 | 2,113 IOPS |
| RAM cache | open+read+close, 3072 B | 20 | 10,698 IOPS |
| RAM cache | open+read+close scaling, 3072 B | 1 -> 20 | 5.1x |
