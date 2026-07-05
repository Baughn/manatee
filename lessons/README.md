# Lesson Corpus

One directory per lesson (EA `docs/examples` format): a Falstad-format
netlist plus a Markdown narrative whose front-matter carries
machine-readable expectations (probe, time, value, tolerance).

One corpus, three consumers (design.md R20): tablet tutorial content,
documentation examples, and CI goldens — every lesson is solved by
manatee-core and by ngspice on every build, and its stated observations
are checked against the solution. A lesson that stops being true fails
the build.

Authoring rules and the 17-lesson arc: `docs/curriculum.md`.
