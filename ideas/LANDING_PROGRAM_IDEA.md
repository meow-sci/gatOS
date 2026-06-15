i want a new rust program written into examples/ called "land-o-matic" which:

- is rust based
- used ratatui as a tui program
- is a guidance program for landing a rocket


here is some notes from a user who built a ksa mod using the same algorithms i want you to use (the source for that is NOT yet available)


> I implemented a launch autopilot using the real guidance algorithm from the space shuttle (Unified Powered Flight Guidance). It's an extremely efficient algorithm designed for 70s hardware, and allows real time re-planning (watch as I change the G-Limit) and the rocket recalculates a solution. Another cool thing about this algo is that it also works for descent - stay tuned!

> Massive credit to Noiredd's PEGAS, which implemented this in KOS a decade a go, with no AI help for the tedious bits! 

The referenced "Noiredd's PEGAS" git repo is cloned to @thirdparty/PEGAS and there's another repo from the same author in @thirdparty/PEGAS-MATLAB for more docs and matlab variations

The G-FOLD algorithm referenced originated from the repo checked out to @thirdparty/G-FOLD

i want you to analyze these algorithms, analyze the data feeds we have from our sim filesystem and our controls in the sim filesystem and come up with a solution that utilizes UPFG + G-FOLD similarly, and uses a ratatui interface for data readouts and inputs that let us change things like the g-limit input to the algorithms

do a deep analysis of the references algorithms, ksa soruces under @thirdparty/ksa/ and our sim filesystem

pay CLOSE ATTENTION to things like the reference frames to ensure the data being read and the calculations and our control inputs do the right calculations and read the right data in the correct reference frames

make a thorough plan and write it to LANDING_PROGRAM_PLAN.md in the repo root