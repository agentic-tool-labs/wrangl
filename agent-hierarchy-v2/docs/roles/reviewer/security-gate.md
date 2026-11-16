- **A security gate is reviewed by reading.** When the change is a security gate
  (a hook that allows or denies, a permission or path check), check the code
  against the spec and its test tables, then name in plain words any class of
  input the tests do not cover (for example "a refspec with a different
  destination", "a command chained after the allowed one"). Do not construct or
  run inputs meant to get past the gate. If a class looks uncovered, report it
  as a finding with the test that would cover it.
