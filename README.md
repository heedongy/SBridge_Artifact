# SBridge (Paper Artifact)


[![DOI](https://zenodo.org/badge/DOI/10.5281/19508301.svg)](https://doi.org/10.5281/zenodo.19508301)

## Setup

> **Note:** Building the image may take a long time and the resulting Docker image size is approximately **67GB** (mainly due to the large RQ4 dataset).
> Please ensure you have sufficient disk space before starting the build.

We provide two options to obtain the Docker image. Choose one of the following methods, then proceed to Step 3.


----
Option A: Pull pre-built image

Instead of building locally, you can directly pull the pre-built image:
 ```bash
     docker pull heedongy/sbridge_artifact:latest
 ```
----

Option B: Build from source
1.	Clone the repository and download the dataset
(Place the dataset in the same directory and do not extract the .tar file)
    ```bash
     git clone https://github.com/heedongy/SBridge_Artifact
     ```
Dataset_link: https://drive.google.com/file/d/1P6zqTmCM8YFwjgnq4-U9gFbg_fTkDnTG/view?usp=sharing

2. Build the image by running either:
   - `./build.sh`
   - or the Docker command:
     ```bash
     docker build -t heedongy/sbridge_artifact:latest .
     ```
   *(Note: Building the image may take a significant amount of time.)*

----

3. Run the container
After completing either Option A or Option B, run:
   - `./run_image.sh`
   - or the Docker command:
     ```bash
     docker run -it --rm --name sbridge_container heedongy/sbridge_artifact:latest /bin/bash
     ```


## Representative sample scripts

We provide lightweight sample scripts for quickly checking that the main functionality of SBridge works as expected. 

1. To run sample function matching:
    - Sample: coreutils, CAT
    
    ```bash
    /home/user/sample_match.sh
    ```
    
2. To run sample vulnerability detection:
    - Sample: CVE-2022-40303
    
    ```bash
    /home/user/sample_vul_detect.sh
    ```
    

## Running function matching

We provide scripts for testing function matching on selected binaries from /home/user/dataset/RQ1, using the datasets from **GNU Coreutils** and **Inetutils**.

- For example, to run the `head` binary of coretuils: `/home/user/coreutils_match.sh [binary]`

```bash
/home/user/coreutils_match.sh head
```

- For example, to run the `dnsdomainname` binary of inetuils: `/home/user/coreutils_match.sh [binary]`
    
    ex : `/home/user/inetutils_match.sh dnsdomainname`
    

##  Running vulnerability detection

We provide scripts for testing vulnerability detection on selected binaries from /home/user/dataset/RQ4

- For example, to run the `CVE-2023-0286` testset of CVE: `/home/user/vul_detect.sh [CVE]`
    
    ```bash
     /home/user/vul_detect.sh CVE-2023-0286
    ```



# SBridge info


 ```tree
project_folder
│
├── input (folder)
│   ├── vulcode (folder)        # source files of the project before the patch (only vulnerable code)
│   ├── patchcode (folder)      # source files of the project after the patch (only function matchin)
│   └── matchcode (folder)     # Source code files used for function matching c
│
├── target (folder)
│   └── binaryfiles   # Unknown binaries (vulnerable, patched, stripped binaries, etc.)
│
└── target.info       # Text file listing vulnerable function names (one name per line)

* Note : If preprocessed files (`*.i`) are available, include them in the input directory for analysis.
 ```


## The usage of SBridge

- The usage of the SBridge tool is as follows:

1. SBridge Build
   ```bash
   dotnet build -c Release
    ```

2. Joren Paser Server Start (port:8081)
    ```bash
    /home/user/SBridge/start_joernserver.sh
    ```


3. Source Code Parsing and Analysis
    ```bash
    /home/user/SBridge/bin/Release/net9.0/sbridge -s <project_path> (--clean) # clean is optional, default is false
    ```
    ex) `/home/user/SBridge/bin/Release/net9.0/sbridge -s dataset/sample/cat --clean`

4. Binary Code Parsing and Analysis (May take a long time)

   - The parsing step using the **Joern parser** can take a significant amount of time.
   - Note that SBridge's own preprocessing does **not** take long.

    ```bash
    /home/user/SBridge/bin/Release/net9.0/sbridge -b <project_path> (--ida) (--clean) # IDA Pro is optional, default is Ghidra
    ```
     ex) `/home/user/SBridge/bin/Release/net9.0/sbridge -b dataset/sample/cat --clean`

5.  SBridge Function Matching (Source to Binary)
    ```bash
    /home/user/SBridge/bin/Release/net9.0/sbridge -m <project_path> (--ida)  # IDA Pro is optional, default is Ghidra
    ```
     ex) `/home/user/SBridge/bin/Release/net9.0/sbridge -m dataset/sample/cat`

6. Vulnerability Detection
    ```bash
    /home/user/SBridge/bin/Release/net9.0/sbridge -d <project_path> (--ida) # IDA Pro is optional, default is Ghidra
    ```
     ex) `/home/user/SBridge/bin/Release/net9.0/sbridge -d dataset/sample/CVE-2018-14470`

## Citation

If you find SBridge useful in your research, please cite our paper:

```bibtex
@article{yang2026sbridge,
  title     = {SBridge: Identifying Source-to-Binary Function Similarity via Cross-Domain Control Block Matching},
  author    = {Yang, Heedong and Lee, Jeongwoo and Yun, Hajin and Woo, Seunghoon},
  journal   = {Proceedings of the ACM on Software Engineering},
  volume    = {3},
  number    = {FSE},
  pages     = {1381--1403},
  year      = {2026},
  publisher = {ACM New York, NY, USA}
}
```
