purrtty represents a terminal emulator with a libghostty-vt headless backend for terminal emulation and a ksa mod UI ontop for KSA game

this is working well, and now I am trying to consider ways to better actually utilize this terminal emulator for in-game use cases

one thing that I would REALLY like is to create a real minimal operation system with some goals:

- i want to be able to leverage an existing package manager system like apt, apk etc from a real distro
- i want it to be as lightweight as possible (it does not need to be a fully fledged general purpose distro with tons of background daemons etc. for things like wifi and networking etc are all mostly unnecessary for our goals)
- i want it to be integrated into the game which is a space flight simulator in meaningful ways. like, for example, an idea would be to expose vehicles data as sensors as like linux file descriptors as if they were some kind of mounted sensor hardware sending a continuous signal stream, etc.  things like that idea (but not necessarily that exactly)

the over-arching goal here is to have an operating system that lets me use existing off the shelf packages that make using a terminal easy like shells, shell job management, unix pipes, common utils like pagers and text editors etc etc.  

but i want absolutely bare bones OS around that to have minimum memory overhead and very fast startup times.  we dont need things like real filesystem drivers, we can deal with potentially just a in memory virtual filesystem etc.

the use case is to use this in-game for in-game things, but, real-world persistence between game sessions may be necesary and should be a real consideration

this doesnt have to be linux (it could be a bsd for example), but I would lean towards a linux or bsd to be able to leverage their package management and posix features (unless there are other ideas out there i am unaware of), but some kind of tiny IoT flavor of linux or something would be perfect.  i prefer the debian/apt ecosystem as well if that is an important consideration.

i am aware of LKL (the linux kernel library) at https://github.com/lkl/linux which is interesting, but i am unsure if it's a good fit (maybe is, maybe isn't). 

The solution MUST be able to work if the host system running the game (and thus this custom operating system) is either on Windows or Linux

Networking would be good to have if that's a choice, but should just be using the host system network stack in a bridge if possible (or a simple virtual driver which maps to the host system networking, something like that?)

Do research, look through the KSA game decompiled sources if needed under @thirdparty/ksa/ and do research on the web if necessary to come up with ideas.

You can propose more then one solution with pros/cons, i want a detailed analysis of options, their strengths and weaknesses, complexity of implementation, etc.

The solution will need to be coded using AI coding agents by a single person as a passion project, so the scope needs to be realistic and manageable while achieving our MVP goals

Now for a concrete use-case I can think of:

This OS runs in the game (one or potentially multiple instances of the OS, depending on how heavy weight it is), and it has devices for things like the vehicles in the simulation, then in-game code or mods can hook into those to drive TUI interfaces in shell sessions that the purrtty terminal emulator provides.  purrtty can render it's screen in the game 3d space as like  screen in a cockpit or on a piece of equipment or a HUD inside a helmet visor, as examples, of how the end-user may interact with it

be thorough, thoughtful and consider everything i've covered and put a detailed analysis into a file OS_ANALYSIS.md in the root of the project
