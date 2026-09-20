# Licensing and distribution

Josour is published for the community under **Apache-2.0**. The root [LICENSE](../LICENSE) is the authoritative
licence for the project's original code and documentation, except where a file explicitly states otherwise.
Third-party material retains its own licence; see [NOTICE](../NOTICE).

You may use, copy, modify, fork, distribute and sell Josour, including as part of a commercial or proprietary
product. There is no licence fee and no requirement to publish your modifications. Redistribution remains subject
to Apache-2.0: give recipients the licence, preserve applicable attribution notices, mark modified files, and
observe the other terms. The licence does not grant trademark rights or imply endorsement of a fork.

The software is provided as is, without warranties or a promise of support, maintenance or security updates.
Sections 7 and 8 of LICENSE disclaim warranties and limit contributors' liability, subject to applicable law.
This is not an absolute exemption from liability that cannot legally be excluded. A distributor offering its own
warranty or support does so on its own behalf, under section 9. This summary adds no restrictions to the licence.

## ملخص بالعربية

نُشر جسور خدمةً للمجتمع تحت رخصة Apache-2.0. يجوز لأي شخص نسخه واستخدامه وتعديله وتطويره وإعادة توزيعه
وبيعه، بما في ذلك الاستخدام التجاري وإدراجه في منتجات مغلقة المصدر، دون رسوم ترخيص أو اشتراط نشر التعديلات.
عند إعادة التوزيع يجب إرفاق الرخصة والحفاظ على إشعارات الحقوق المنطبقة وبيان الملفات المعدلة والالتزام بباقي
شروط الرخصة. لا يمنح ذلك حق الادعاء بأن صاحب المشروع يؤيد نسخة معدلة أو يضمنها.

يُقدَّم البرنامج كما هو، دون ضمان أو التزام بالدعم أو الصيانة أو التحديثات الأمنية. تتحدد مسؤولية أصحاب الحقوق
والمساهمين وفق الرخصة وبالقدر الذي يسمح به القانون؛ ولا يمكن ضمان الإعفاء من مسؤولية لا يسمح القانون باستبعادها.
هذا ملخص توضيحي لا يضيف شروطًا إلى النص الأصلي للرخصة. وتظل مكونات الأطراف الأخرى خاضعة لتراخيصها الخاصة.

## Where the notices travel

- Source checkout: root `LICENSE` and `NOTICE`.
- Desktop app, including the standalone Windows EXE, installer and macOS bundle: both texts are embedded in the
  application assembly and readable offline through **About → License and notices** (حول التطبيق ← الترخيص وإشعارات الحقوق).
- Python wheels and source distributions: each package includes `LICENSE` and `NOTICE`; wheel metadata declares
  `License-Expression: Apache-2.0` and includes the texts in its `.dist-info/licenses` directory.
- API and relay Docker images: `/app/LICENSE` and `/app/NOTICE`, also retained in the installed Python package metadata.

The root files are the source of truth. After editing them, run `python3 scripts/check-license-files.py --sync` to
refresh the copies needed for standalone Python builds and Docker build contexts. CI checks that they match.

## Third-party dependencies

`NOTICE` includes the MIT notice for Fluent System Icons copied into this repository. It is not an exhaustive
notice bundle for NuGet/pip dependencies or native runtime components. Changing Josour's licence does not relicense
those components. When preparing a binary release, inventory the actual resolved dependencies (including transitive
and native components), retain their applicable licence and attribution texts, and distribute them with the release.
Do not assume a package's licence is satisfied merely because a package manager downloaded it during the build.

References: [Apache-2.0 terms](https://www.apache.org/licenses/LICENSE-2.0),
[Python package licence metadata](https://packaging.python.org/en/latest/guides/writing-pyproject-toml/#license-and-license-files).
