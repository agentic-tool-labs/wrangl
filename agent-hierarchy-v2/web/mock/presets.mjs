// The mock's named fixture sizes: the one table of member, open-exchange and answered-exchange counts. AH_MOCK_FIXTURE selects a
// row by name; the E4 harness selects by name too and keeps no counts of its own.
//   small      a modest team, the size the E4 CPU and idle-bytes baseline was recorded at
//   realistic  a busy home: one team of 50 members, 1,500 answered and 40 open exchanges, excerpts at the daemon's 4 KB cap
export const PRESETS = {
  small: { AH_MOCK_MEMBERS: 10, AH_MOCK_OPEN: 8, AH_MOCK_DONE: 33 },
  realistic: { AH_MOCK_MEMBERS: 50, AH_MOCK_OPEN: 40, AH_MOCK_DONE: 1500, AH_MOCK_EXCERPT_BYTES: 4096 },
};
