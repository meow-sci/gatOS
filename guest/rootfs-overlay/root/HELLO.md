Hello, welcome to gatOS!

You're logged in as `root`. The SSH session you reach from purrTTY is key-based, but `root` also
has a well-known password — `gatos` — handy for `su`, a local console, or running your own services.

Some common Alpine packages like curl, wget, vim, neovim, git, bash, zsh, less have been pre-installed for convenience

If you're looking for a quick way to setup Zsh with some nice prompt features, you can use a script I made specifically for gatOS which will

- setup zsh as default shell
- install and setup starship
- install and setup atuin

```sh
git clone https://github.com/meow-sci/gatos-scripts.git
cd gatos-scripts/alex
./initial-shell-setup.sh
```

Then close this gatOS terminal window and open a new one, enjoy!

