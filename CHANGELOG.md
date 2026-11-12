# Changelog

## [0.4.0](https://github.com/Alexxfromgit/EmmaSpace/compare/v0.3.0...v0.4.0) (2026-11-12)


### Features

* explain locked, unmounted and backup drives on macOS ([#26](https://github.com/Alexxfromgit/EmmaSpace/issues/26)) ([877b4a6](https://github.com/Alexxfromgit/EmmaSpace/commit/877b4a660aae90d77b7b2481332145b2efee0b05))
* explain purgeable space on macOS, with a link to Storage settings ([32d4648](https://github.com/Alexxfromgit/EmmaSpace/commit/32d4648d1e14f8bf231d5e7bec8eec3cc01a1fef))


### Bug Fixes

* keep Emma's picture whole in Settings by laying settings out side by side ([32d4648](https://github.com/Alexxfromgit/EmmaSpace/commit/32d4648d1e14f8bf231d5e7bec8eec3cc01a1fef))
* match Finder's free space and size units on macOS ([32d4648](https://github.com/Alexxfromgit/EmmaSpace/commit/32d4648d1e14f8bf231d5e7bec8eec3cc01a1fef))

## [0.3.0](https://github.com/Alexxfromgit/EmmaSpace/compare/v0.2.0...v0.3.0) (2026-11-11)


### Features

* add a Settings window with a theme choice and automatic update checks ([#15](https://github.com/Alexxfromgit/EmmaSpace/issues/15)) ([550130e](https://github.com/Alexxfromgit/EmmaSpace/commit/550130e14ead87b2274c809d8f570db66743df97))
* count APFS clones once in disk usage on macOS ([#22](https://github.com/Alexxfromgit/EmmaSpace/issues/22)) ([be3e5cb](https://github.com/Alexxfromgit/EmmaSpace/commit/be3e5cb802c8c3aba550bbc227aab52e4236e542))
* fast scan for ext4 on Linux ([#13](https://github.com/Alexxfromgit/EmmaSpace/issues/13)) ([cf48b3f](https://github.com/Alexxfromgit/EmmaSpace/commit/cf48b3fe1b13049cdb526bd662081ce949248e2c))
* line up table headers with their columns and let users hide columns ([#17](https://github.com/Alexxfromgit/EmmaSpace/issues/17)) ([59d2a8d](https://github.com/Alexxfromgit/EmmaSpace/commit/59d2a8ded2fdd4d797b316ab560b8be131dff88e))
* point out missing Full Disk Access on macOS ([#23](https://github.com/Alexxfromgit/EmmaSpace/issues/23)) ([d633fa9](https://github.com/Alexxfromgit/EmmaSpace/commit/d633fa9574d481d631a5f787c98ebbe3e5cd3197))
* save duplicate results in snapshots and speed up repeat duplicate searches ([#20](https://github.com/Alexxfromgit/EmmaSpace/issues/20)) ([82cd961](https://github.com/Alexxfromgit/EmmaSpace/commit/82cd9616447061c18f94702152b48236649f7ffd))
* show Emma's painting faintly behind the start page ([#18](https://github.com/Alexxfromgit/EmmaSpace/issues/18)) ([0849ced](https://github.com/Alexxfromgit/EmmaSpace/commit/0849ced8b8622833a5dc19148a088c017ba76151))


### Bug Fixes

* open the main window shorter, at 700 px instead of 840 ([#16](https://github.com/Alexxfromgit/EmmaSpace/issues/16)) ([5f95b0b](https://github.com/Alexxfromgit/EmmaSpace/commit/5f95b0b61da0aefcde05898182b8dbc80021ccfc))
* save snapshots on macOS with a single .emmasnap extension ([#11](https://github.com/Alexxfromgit/EmmaSpace/issues/11)) ([599af2c](https://github.com/Alexxfromgit/EmmaSpace/commit/599af2cf73567048ebb52bb018a25ecb95bc7f96))
* show "Check automatically" unticked where the app can't update ([#14](https://github.com/Alexxfromgit/EmmaSpace/issues/14)) ([bbd4193](https://github.com/Alexxfromgit/EmmaSpace/commit/bbd41934764985455b43c91a8fee4b1aaac75938))


### Performance

* read folders in bulk on macOS for faster scans ([#21](https://github.com/Alexxfromgit/EmmaSpace/issues/21)) ([e90fd1d](https://github.com/Alexxfromgit/EmmaSpace/commit/e90fd1d6546507a9a885dbc81083b3a751a88578))

## [0.2.0](https://github.com/Alexxfromgit/EmmaSpace/compare/v0.1.0...v0.2.0) (2026-11-09)


### Features

* new app icon and README artwork ([#5](https://github.com/Alexxfromgit/EmmaSpace/issues/5)) ([2d1e151](https://github.com/Alexxfromgit/EmmaSpace/commit/2d1e1517501dcd040615a68998da49b56c80156e))
* one scan button that reads Rescan for the results on screen, plus F5 and Ctrl+R / ⌘R to rescan ([40d889a](https://github.com/Alexxfromgit/EmmaSpace/commit/40d889a5fd91fd68c440a88ebe4fabe6916299e3))
* open snapshots with a double-click, with their own file icon ([#7](https://github.com/Alexxfromgit/EmmaSpace/issues/7)) ([e2ee28e](https://github.com/Alexxfromgit/EmmaSpace/commit/e2ee28ef0966915b1edb98656431be00cda7d71d))
* show Emma in confirmation and error dialogs ([#8](https://github.com/Alexxfromgit/EmmaSpace/issues/8)) ([48e29fd](https://github.com/Alexxfromgit/EmmaSpace/commit/48e29fd702256b96b675c1871621bf00d2e6237f))


### Bug Fixes

* drive cards highlight with their rounded shape on hover ([40d889a](https://github.com/Alexxfromgit/EmmaSpace/commit/40d889a5fd91fd68c440a88ebe4fabe6916299e3))
* show in folder (show in Finder on macOS) opens the item's own folder with the item selected, also for paths with spaces ([40d889a](https://github.com/Alexxfromgit/EmmaSpace/commit/40d889a5fd91fd68c440a88ebe4fabe6916299e3))
* status bar captions line up with the version button ([40d889a](https://github.com/Alexxfromgit/EmmaSpace/commit/40d889a5fd91fd68c440a88ebe4fabe6916299e3))

## 0.1.0 (2026-11-07)


### Features

* disk space analyzer with an NTFS master file table fast scan, parallel native scanners for Windows, macOS and Linux, folder tree, cushion treemap, file types, largest files, duplicates, clean-up, snapshots with compare, and HTML/CSV export ([c1f53bf](https://github.com/Alexxfromgit/EmmaSpace/commit/c1f53bf179a0ce5261b95b5b4288ef8462e42983))
* installers for Windows (x64, ARM64), macOS (Apple silicon, Intel) and Linux with automatic in-app updates ([c1f53bf](https://github.com/Alexxfromgit/EmmaSpace/commit/c1f53bf179a0ce5261b95b5b4288ef8462e42983))
