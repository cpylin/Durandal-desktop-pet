#!/bin/bash
# build.sh —— 用 Windows 自带的 csc.exe 编译，无需安装任何工具链
#
# 用法（在 Git Bash 里）：
#   cd /d/ClaudePet && ./build.sh
#
# 本机编译器只支持 C# 5：不要用字符串插值 $""、?.、nameof、表达式体成员。
#
# 两个已踩过的坑（改动本脚本时注意）：
#   1. csc 把 "/" 当作选项前缀，即使出现在路径中间。
#      "D:/a/b.cs" 会被解析成 "D:" + "b.cs" —— 必须用反斜杠。
#   2. Git Bash 会把 /nologo 这类参数转换成路径，所以要设 MSYS_NO_PATHCONV=1。

set -e

CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
GAC="C:/Windows/Microsoft.NET/assembly"

export MSYS_NO_PATHCONV=1

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

if [ ! -f "$CSC" ]; then
  echo "错误：找不到 csc.exe，请确认 .NET Framework 4.x 已安装。" >&2
  exit 1
fi

# 程序集引用（路径里不含空格，可以安全用正斜杠）
WPF=(
  "-r:$GAC/GAC_MSIL/PresentationFramework/v4.0_4.0.0.0__31bf3856ad364e35/PresentationFramework.dll"
  "-r:$GAC/GAC_64/PresentationCore/v4.0_4.0.0.0__31bf3856ad364e35/PresentationCore.dll"
  "-r:$GAC/GAC_MSIL/WindowsBase/v4.0_4.0.0.0__31bf3856ad364e35/WindowsBase.dll"
  "-r:$GAC/GAC_MSIL/System.Xaml/v4.0_4.0.0.0__b77a5c561934e089/System.Xaml.dll"
)
# 注意：System.Web.Extensions（JSON 解析）不用显式引用。
# csc 会自动读取自带的 csc.rsp，其中已经引用了它，
# 再显式引一次会报 CS1703「重复的程序集」。

COMMON="-nologo -platform:x64 -optimize+ -warn:4"

# 共用源码。csc 支持一次编译多个源文件 —— Common.cs 会被**编进**每个 exe，
# 所以它们依然各自独立、不需要任何额外的 DLL 就能单独拷走运行。
#
# 只有 PetNotify 不带它：Common.cs 用到 WPF 类型，而 PetNotify 跑在 hook 热路径上，
# 刻意不引用 WPF 来加快启动（它也没有任何图片操作，用不上这里的东西）。
SHARED="src\\Common.cs"

echo "[1/6] MakeSprites.exe  (占位素材生成器)"
"$CSC" $COMMON -target:exe -out:"bin\\MakeSprites.exe" "src\\MakeSprites.cs" "$SHARED" "${WPF[@]}"

echo "[2/6] Cutout.exe       (抠图：把人物从背景里拿出来)"
"$CSC" $COMMON -target:exe -out:"bin\\Cutout.exe" "src\\Cutout.cs" "$SHARED" "${WPF[@]}"

echo "[3/6] SheetGen.exe     (把抠好的图做成带动作的 sprite sheet)"
"$CSC" $COMMON -target:exe -out:"bin\\SheetGen.exe" "src\\SheetGen.cs" "$SHARED" "${WPF[@]}"

echo "[4/6] Zoom.exe         (调试：裁一小块放大，检查边缘和对齐)"
"$CSC" $COMMON -target:exe -out:"bin\\Zoom.exe" "src\\Zoom.cs" "$SHARED" "${WPF[@]}"

echo "[5/6] PetNotify.exe    (hook 热路径，无 WPF 依赖 —— 所以不带 Common.cs)"
"$CSC" $COMMON -target:exe -out:"bin\\PetNotify.exe" "src\\PetNotify.cs"

echo "[6/6] Pet.exe          (桌宠主程序)"
"$CSC" $COMMON -target:winexe -out:"bin\\Pet.exe" "src\\Pet.cs" "$SHARED" "${WPF[@]}"

echo
echo "编译完成："
ls -la bin/*.exe | awk '{printf "  %-18s %7d 字节\n", $NF, $5}'
