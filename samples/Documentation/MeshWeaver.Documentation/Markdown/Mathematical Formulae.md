---
Title: "Using Mathematical Formulae"
Abstract: >
    In this article we show how to write mathematical formulae in interactive Markdown.
Thumbnail: "images/Formulas.jpg"
Published: "2025-03-20"
Authors:
  - "Roland Bürgi"
Tags:
  - "Documentation"
  - "Conceptual"
  - "Mathematics"
---

In MeshWeaver, we have implemented [MathJax](https://www.mathjax.org/) to 
lay out mathematical formulae. Most of the conventions of [LaTeX](https://www.latex-project.org/).

You can have inlined formlua excerps or just variables. For instance, typing

```latex
inline formula $E[X] = \int_{-\infty}^{\infty} dx (1-F(x))$
```

will result in 

inline $E[X] = \int_{-\infty}^{\infty} dx (1-F(x))$. 

If you want to put the formula on a single line, you
will have to type

```latex
$$
E[X] = \int_{-\infty}^{\infty} dx (1-F(x))
$$
```
will result in 

$$
E[X] = \int_{-\infty}^{\infty} dx (1-F(x))
$$

As you can see, this variant will give the formula more space.
Dollar signs in the middle of words, such as a$sign will not be 
interpreted as formula delimiter. You can use a normal \$ character by escaping it:

```
this is a \$ character
```