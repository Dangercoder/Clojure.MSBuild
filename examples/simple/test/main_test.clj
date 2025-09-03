(ns main-test
  (:require [clojure.test :refer :all]
            [main :refer [get-status]]))

(deftest test-get-status
  (testing "get-status returns success"
    (let [result (get-status)]
      (is (= :success (:status result)))
      (is (map? result))
      (is (= {:status :success} result)))))

(deftest test-arithmetic
  (testing "Basic arithmetic operations"
    (is (= 2 (+ 1 1)) "1 + 1 should equal 2")
    (is (= 4 (* 2 2)) "2 * 2 should equal 4")))

(deftest test-strings
  (testing "String operations"
    (is (= "hello world" (str "hello" " " "world")) "String concatenation")
    (is (= 11 (count "hello world")) "String length")))